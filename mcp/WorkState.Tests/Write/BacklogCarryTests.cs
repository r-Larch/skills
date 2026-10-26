using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// ws/12. Deferring to "backlog" made a backlog phase inside the run, and that phase then refused the
/// run's close: "1 child item(s) still open: backlog". "backlog" now means a project-level run, and a
/// run still holding an in-run backlog from before is closable — the close carries its work out.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class BacklogCarryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Deferring_to_backlog_lands_in_a_project_level_run_and_the_run_then_closes()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var deferred = await Defer(world, "1.1");
        Assert.False(deferred.Refused, deferred.Text);

        var carried = await world.ItemAsync("1.1-d");
        Assert.Equal(ItemStatus.Open, carried.Status);

        await using var db = fixture.NewContext();
        var holder = await db.Items.SingleAsync(i => i.Id == carried.ParentId, Ct);

        Assert.Equal("run", holder.Kind);
        Assert.Equal(world.ProjectId, holder.ParentId);
        Assert.Equal("backlog", holder.Ref);
        Assert.True(await db.RunConfigs.AnyAsync(c => c.ItemId == holder.Id && c.ProjectItemId == world.ProjectId, Ct));
        Assert.False(await db.Items.AnyAsync(i => i.ParentId == world.RunId && i.Ref == "backlog", Ct));

        await CloseOtherChildrenAsync(world);
        var closed = await CloseRunAsync(world);

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("run-a"));
        Assert.Equal(ItemStatus.Open, (await world.ItemAsync("1.1-d")).Status);
    }

    [Fact]
    public async Task A_run_holding_an_in_run_backlog_from_before_closes_and_carries_its_work_to_the_project()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var (phase, followup) = await InRunBacklogAsync(world);
        await CloseOtherChildrenAsync(world);

        var closed = await CloseRunAsync(world);

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("run-a"));

        await using var db = fixture.NewContext();
        var carried = await db.Items.SingleAsync(i => i.Id == followup, Ct);
        var holder = await db.Items.SingleAsync(i => i.Id == carried.ParentId, Ct);

        Assert.Equal(ItemStatus.Open, carried.Status);
        Assert.Equal("1.1-d", carried.Ref);
        Assert.Equal("run", holder.Kind);
        Assert.Equal(world.ProjectId, holder.ParentId);
        Assert.False(
            await db.Items.AnyAsync(
                i => i.WorkspaceId == world.WorkspaceId && i.Id != holder.Id && i.Ref == holder.Ref, Ct),
            $"{holder.Ref} collides with another ref in the project");

        Assert.Contains($"carried 1 open item(s) to {holder.Ref}/: 1.1-d", closed.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, (await db.Items.SingleAsync(i => i.Id == phase, Ct)).Status);
    }

    [Fact]
    public async Task A_run_with_another_open_child_still_refuses_the_close_and_carries_nothing()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var (phase, followup) = await InRunBacklogAsync(world);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.RunId, ItemStatus.Review);

        var refused = await CloseRunAsync(world);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("still open", refused.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.Equal(phase, (await db.Items.SingleAsync(i => i.Id == followup, Ct)).ParentId);
        Assert.Equal(ItemStatus.Open, (await db.Items.SingleAsync(i => i.Id == phase, Ct)).Status);
        Assert.Equal(1, await db.Items.CountAsync(i => i.ParentId == world.ProjectId && i.Kind == "run", Ct));
    }

    [Fact]
    public async Task An_existing_project_level_backlog_run_is_reused_rather_than_a_new_one_made()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world, "backlog");

        await using var before = fixture.NewContext();
        var existing = await before.Items.SingleAsync(
            i => i.ParentId == world.ProjectId && i.Kind == "run" && i.Ref == "backlog", Ct);

        var deferred = await Defer(world, "1.1");

        Assert.False(deferred.Refused, deferred.Text);

        await using var db = fixture.NewContext();
        var carried = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "1.1-d", Ct);

        Assert.Equal(existing.Id, carried.ParentId);
        Assert.Equal(2, await db.Items.CountAsync(i => i.ParentId == world.ProjectId && i.Kind == "run", Ct));
    }

    /// <summary>
    /// ws/5's edge, moved: a cancelled in-run "backlog" phase made the deferral collide with it. The
    /// deferral no longer goes into the run, so it succeeds — into the project backlog, which takes a
    /// ref that cannot collide with the cancelled phase.
    /// </summary>
    [Fact]
    public async Task Deferring_past_a_cancelled_in_run_backlog_lands_in_the_project_backlog()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var cancelled = await WriteSeed.ChildAsync(world, world.RunId, "backlog", kind: "phase", status: ItemStatus.Cancelled);

        var deferred = await Defer(world, "1.1");

        Assert.False(deferred.Refused, deferred.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));

        await using var db = fixture.NewContext();
        var carried = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "1.1-d", Ct);
        var holder = await db.Items.SingleAsync(i => i.Id == carried.ParentId, Ct);

        Assert.Equal("run", holder.Kind);
        Assert.Equal(world.ProjectId, holder.ParentId);
        Assert.NotEqual(cancelled, holder.Id);
        Assert.NotEqual("backlog", holder.Ref);
    }

    private static Task<WriteResult> Defer(WriteWorld world, string reference)
        => world.Writer.StatusAsync(
            world.WorkspaceId, reference, "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "not this run", Target: "backlog"), Ct);

    /// <summary>What a pre-ws/12 deferral left behind: an open in-run backlog phase holding 1.1-d.</summary>
    private static async Task<(Guid Phase, Guid Followup)> InRunBacklogAsync(WriteWorld world)
    {
        var phase = await WriteSeed.ChildAsync(world, world.RunId, "backlog", kind: "phase");
        var followup = await WriteSeed.ChildAsync(world, phase, "1.1-d", kind: "followup");
        return (phase, followup);
    }

    private static async Task CloseOtherChildrenAsync(WriteWorld world)
    {
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, (await world.ItemAsync("2")).Id, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.RunId, ItemStatus.Review);
    }

    private static Task<WriteResult> CloseRunAsync(WriteWorld world)
        => world.Writer.StatusAsync(
            world.WorkspaceId, "run-a", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);
}
