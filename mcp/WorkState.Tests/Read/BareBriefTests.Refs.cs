using System.Text.RegularExpressions;
using WorkState.Core.Board;
using WorkState.Core.Writing;
using WorkState.Tests.App;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// mature/refs-rest — the three places bare-brief left printing a bare ref: work_get's head row,
/// a missing scope's nearby suggestions, and the item resource's ambiguity order.
/// </summary>
public sealed partial class BareBriefTests
{
    [Fact]
    public async Task Work_get_head_row_of_a_ref_two_runs_share_is_callable_as_printed()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var task = await WriteSeed.SecondRunAsync(world);
        var runB = await RunOfAsync(world, task);
        await WriteSeed.ChildAsync(world, runB, "1", kind: "phase");

        var home = await world.ScopeAsync("run-a");
        var text = await world.Reader.GetAsync(home, ["run-b/1"], [], Ct);
        var head = text.Split('\n')[0].Split(' ')[0];

        Assert.Equal("run-b/1", head);

        var again = await world.Reader.GetAsync(home, [head], [], Ct);
        Assert.DoesNotContain("✗", again, StringComparison.Ordinal);
        Assert.Contains("child 1", again.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_scope_suggests_only_calls_that_resolve_as_printed()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var task = await WriteSeed.SecondRunAsync(world);
        await WriteSeed.ChildAsync(world, await RunOfAsync(world, task), "1", kind: "phase");

        var missing = Assert.IsType<ScopeResult.Missing>(await world.Reader.ResolveAsync(world.Slug, "Foundations", Ct));
        var text = ScopeExplain.Text(missing, "work_brief", "Foundations");
        var scopes = ScopeCalls().Matches(text).Select(m => m.Groups[1].Value).ToList();

        Assert.Contains("run-a/1", scopes);
        Assert.DoesNotContain("1", scopes);

        var board = new WorkState.Core.Queries.BoardQueries(fixture.DataSource);
        foreach (var scope in scopes)
        {
            Assert.Single(await board.ResolveAsync(world.WorkspaceId, scope, Ct));
        }

        var found = Assert.IsType<ScopeResult.Found>(await world.Reader.ResolveAsync(world.Slug, "run-a/1", Ct));
        Assert.Equal(world.PhaseId, found.Item.Id);
    }

    /// <summary>
    /// "1" is a phase of run-a and of other-run. The workspace's busiest run is run-a, so the item
    /// resource used to name run-a's first to every caller; a session whose repo is other's now gets
    /// other's first, as its tools do, and a static token keeps the workspace order.
    /// </summary>
    [Fact]
    public async Task The_item_resource_names_the_callers_own_project_first_for_an_ambiguous_ref()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ProjectAsync(world, "other", new RepoArg(Remote, Root));
        var phase = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("phase", "Other's first phase", Ref: "1", Parent: "other-run")], Ct);
        Assert.False(phase.Refused, phase.Text);

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var minted = await server.ConnectAsync(await MintAsync(server, "aw-item-home", new { remote = Remote }), Ct);
        await using var shared = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var own = Candidates(await ResourceAsync(minted, "work://item/1"));
        var workspace = Candidates(await ResourceAsync(shared, "work://item/1"));

        Assert.Equal("other-run/1", own[0]);
        Assert.Equal("run-a/1", workspace[0]);
        Assert.Equal(own.Order(), workspace.Order());
    }

    private static List<string> Candidates(string refusal)
    {
        var call = Assert.Single(GetCall().Matches(refusal)).Groups[1].Value;
        return [.. Regex.Matches(call, "\"([^\"]+)\"").Select(m => m.Groups[1].Value)];
    }

    private async Task<Guid> RunOfAsync(WriteWorld world, Guid taskId)
    {
        await using var db = fixture.NewContext();
        return db.Items.Single(i => i.Id == taskId).ParentId!.Value;
    }

    [GeneratedRegex("work_brief\\(scope:\"([^\"]+)\"\\)")]
    private static partial Regex ScopeCalls();

    [GeneratedRegex("work_get\\(refs:\\[([^\\]]+)\\]\\)")]
    private static partial Regex GetCall();
}
