using Dapper;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// m62. <c>work_move</c> keeps an item under a kind its <c>item_kind.allowed_parent_kinds</c> names, and a kind with an
/// empty list — a project — at the root. Project access keys on the root, so a project moved under another project's
/// run would take that project's membership.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class MoveParentKindTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MoveParentKind_a_project_moved_under_another_projects_run_is_refused_and_keeps_its_membership()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var before = await MembersAsync(world, world.P1Id);

        // User2 is a member of both projects: only the kind check stands between P1 and P2's run.
        await using var both = await world.McpAsync((await world.CredentialsAsync(world.User2))[1].Mcp!);
        var moved = await ReadServer.CallEitherAsync(both, "work_move", Args(("ref", ProjectWorld.P1), ("parent", $"{ProjectWorld.P2}/run-a")), Ct);

        Assert.True(moved.Refused, moved.Text);
        RefusalShape.SelfCorrecting(moved.Text);
        Assert.Contains("a project is a root", moved.Text, StringComparison.Ordinal);
        Assert.Contains($"work_move(ref:\"{ProjectWorld.P1}\", before:", moved.Text, StringComparison.Ordinal);

        // User1 is no member of P2, and is refused without being shown it.
        await using var outsider = await world.McpAsync((await world.CredentialsAsync(world.User1))[1].Mcp!);
        var hidden = await ReadServer.CallEitherAsync(outsider, "work_move", Args(("ref", ProjectWorld.P1), ("parent", $"{ProjectWorld.P2}/run-a")), Ct);
        Assert.True(hidden.Refused, hidden.Text);
        Assert.DoesNotContain(ProjectWorld.TitleP2, hidden.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.Null((await db.Items.SingleAsync(i => i.Id == world.P1Id, Ct)).ParentId);
        Assert.Equal(before, await MembersAsync(world, world.P1Id));
        Assert.NotEmpty(before);
    }

    [Fact]
    public async Task MoveParentKind_a_task_moved_between_two_runs_still_moves()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        var runB = (await world.ItemAsync("run-b")).Id;

        var there = await world.Writer.MoveAsync(world.WorkspaceId, WriteSeed.Orchestrator, "1.1", "run-b", null, null, Ct);
        Assert.False(there.Refused, there.Text);
        Assert.Equal(runB, (await world.ItemAsync("1.1")).ParentId);

        var back = await world.Writer.MoveAsync(world.WorkspaceId, WriteSeed.Orchestrator, "9.1", "run-a", null, null, Ct);
        Assert.False(back.Refused, back.Text);
        Assert.Equal(world.RunId, (await world.ItemAsync("9.1")).ParentId);
    }

    [Fact]
    public async Task MoveParentKind_a_task_moved_before_a_project_to_the_top_level_is_refused()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        string project;
        await using (var db = fixture.NewContext())
        {
            project = (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
        }

        var moved = await world.Writer.MoveAsync(world.WorkspaceId, WriteSeed.Orchestrator, "1.1", null, project, null, Ct);

        Assert.True(moved.Refused, moved.Text);
        RefusalShape.SelfCorrecting(moved.Text);
        Assert.Contains("a task sits under a phase or a run", moved.Text, StringComparison.Ordinal);
        Assert.Contains("work_move(ref:\"1.1\", parent:", moved.Text, StringComparison.Ordinal);
        Assert.Equal(world.PhaseId, (await world.ItemAsync("1.1")).ParentId);
    }

    private static async Task<List<Guid>> MembersAsync(ProjectWorld world, Guid project)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<Guid>(
            "SELECT user_id FROM project_member WHERE project_item_id = @project ORDER BY user_id;",
            new { project })).AsList();
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);
}
