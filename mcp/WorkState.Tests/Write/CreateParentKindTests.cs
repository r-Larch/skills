using Microsoft.EntityFrameworkCore;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// m63. <c>item_kind.allowed_parent_kinds</c> is the one rule for where an item may sit, and <c>work_create</c>,
/// <c>work_move</c> and <c>work_import</c> all obey it: only a project is a root, and a task may sit under a task.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class CreateParentKindTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("feature", "project")]
    [InlineData("run", "project or feature")]
    [InlineData("task", "phase or run or task")]
    public async Task CreateParentKind_a_parentless_item_that_is_no_project_is_refused(string kind, string allowed)
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg(kind, $"A root {kind}", VerifyCmd: "dotnet test", Criteria: ["it holds"])], Ct);

        Assert.True(created.Refused, created.Text);
        RefusalShape.SelfCorrecting(created.Text);
        Assert.Contains("only a project is a root", created.Text, StringComparison.Ordinal);
        Assert.Contains($"work_create(items:[{{kind:\"{kind}\", parent:\"<a {allowed} ref>\"", created.Text, StringComparison.Ordinal);
        await AssertAbsentAsync(world, $"A root {kind}");
    }

    [Fact]
    public async Task CreateParentKind_a_project_with_a_parent_is_refused()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", "A nested project", Parent: "run-a")], Ct);

        Assert.True(created.Refused, created.Text);
        RefusalShape.SelfCorrecting(created.Text);
        Assert.Contains("a project is a root; omit parent", created.Text, StringComparison.Ordinal);
        await AssertAbsentAsync(world, "A nested project");
    }

    [Fact]
    public async Task CreateParentKind_a_task_under_a_project_is_refused_with_a_route_to_a_run_or_phase()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "Straight under the project", Parent: await ProjectRefAsync(world),
                VerifyCmd: "dotnet test", Criteria: ["it holds"])], Ct);

        Assert.True(created.Refused, created.Text);
        RefusalShape.SelfCorrecting(created.Text);
        Assert.Contains("a task sits under a phase or a run or a task", created.Text, StringComparison.Ordinal);
        Assert.Contains("parent:\"<a phase or run or task ref>\"", created.Text, StringComparison.Ordinal);
        await AssertAbsentAsync(world, "Straight under the project");
    }

    [Fact]
    public async Task CreateParentKind_a_task_under_a_task_is_created_and_moves_between_tasks()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var other = await WriteSeed.SecondRunAsync(world);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "A nested task", Parent: "1.1", VerifyCmd: "dotnet test", Criteria: ["it holds"])], Ct);
        Assert.False(created.Refused, created.Text);

        await using (var db = fixture.NewContext())
        {
            Assert.Equal(world.TaskId, (await db.Items.SingleAsync(i => i.Title == "A nested task" && i.WorkspaceId == world.WorkspaceId, Ct)).ParentId);
        }

        var nested = (await NestedAsync(world)).Ref;
        var moved = await world.Writer.MoveAsync(world.WorkspaceId, WriteSeed.Orchestrator, nested, "9.1", null, null, Ct);
        Assert.False(moved.Refused, moved.Text);
        Assert.Equal(other, (await NestedAsync(world)).ParentId);
    }

    [Fact]
    public async Task CreateParentKind_import_refuses_what_create_refuses()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);
        var bound = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg($"git@github.com:r-larch/parent-kind-{world.WorkspaceId:n}.git")), Ct);
        Assert.False(bound.Refused, bound.Text);

        var task = new ImportItemArg(new ImportSourceArg("docs/plan.md", "## Task"), "task", "still open",
            Title: "Imported straight under the project", Parent: project, VerifyCmd: "dotnet test", Criteria: ["it holds"]);

        var imported = await world.Writer.ImportAsync(world.WorkspaceId, WriteSeed.Orchestrator, project, [task], false, Ct);
        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "Created straight under the project", Parent: project, VerifyCmd: "dotnet test", Criteria: ["it holds"])], Ct);

        Assert.True(imported.Refused, imported.Text);
        Assert.True(created.Refused, created.Text);
        RefusalShape.SelfCorrecting(imported.Text);
        Assert.Contains("a task sits under a phase or a run or a task", imported.Text, StringComparison.Ordinal);
        Assert.Contains("work_import(project:", imported.Text, StringComparison.Ordinal);
        Assert.Contains("parent:\"<a phase or run or task ref>\"", imported.Text, StringComparison.Ordinal);

        var nested = await world.Writer.ImportAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            [task with { Title = "Imported under a task", Parent = "1.1" }], false, Ct);
        Assert.False(nested.Refused, nested.Text);
        await AssertAbsentAsync(world, "Imported straight under the project");
    }

    private async Task<Core.Domain.Item> NestedAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return await db.Items.SingleAsync(i => i.Title == "A nested task" && i.WorkspaceId == world.WorkspaceId, Ct);
    }

    private async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
    }

    private async Task AssertAbsentAsync(WriteWorld world, string title)
    {
        await using var db = fixture.NewContext();
        Assert.False(await db.Items.AnyAsync(i => i.WorkspaceId == world.WorkspaceId && i.Title == title, Ct));
    }
}
