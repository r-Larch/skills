using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 07 §5. The machine answer to "a parked agent is indistinguishable from a busy one": it stops
/// writing, its lease lapses, and the store says so. Both Postgres guarantees are exercised in both
/// directions — the refusal and the parallel run that must still proceed.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClaimTests(PostgresFixture fixture)
{
    [Fact]
    public async Task In_progress_takes_a_claim_and_a_second_agent_is_refused_by_name()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var first = await Start(world, "1.1", WriteSeed.Orchestrator);
        Assert.False(first.Refused, first.Text);
        Assert.Contains("claim", first.Text, StringComparison.Ordinal);

        var second = await Start(world, "1.1", WriteSeed.Other);

        Assert.True(second.Refused);
        RefusalShape.SelfCorrecting(second.Text);
        Assert.Contains(WriteSeed.Orchestrator.Name, second.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
    }

    [Fact]
    public async Task The_holder_retaking_its_own_item_extends_the_lease_rather_than_refusing()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await Start(world, "1.1", WriteSeed.Orchestrator);
        var again = await Start(world, "1.1", WriteSeed.Orchestrator);

        Assert.False(again.Refused, again.Text);
        Assert.Contains("extended", again.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();
        Assert.Equal(1, await db.Claims.CountAsync(c => c.ItemId == world.TaskId && c.ReleasedAt == null, Ct));
    }

    /// <summary>
    /// The parallel-orchestrator rule: two runs in different worktrees proceed concurrently, two
    /// sharing a working tree do not. Refusing both would be the over-correction that stops the
    /// queue this whole product exists to run.
    /// </summary>
    [Fact]
    public async Task One_writer_per_working_tree_but_another_tree_proceeds_in_parallel()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await WriteSeed.SecondRunAsync(world);

        var held = await Start(world, "1.1", WriteSeed.Orchestrator);
        Assert.False(held.Refused, held.Text);

        var sameTree = await Start(world, "1.2", WriteSeed.Other);

        Assert.True(sameTree.Refused);
        RefusalShape.SelfCorrecting(sameTree.Text);
        Assert.Contains("working tree", sameTree.Text, StringComparison.Ordinal);
        Assert.Contains("1.1", sameTree.Text, StringComparison.Ordinal);

        var otherTree = await Start(world, "9.1", WriteSeed.Other);

        Assert.False(otherTree.Refused, otherTree.Text);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("9.1"));
    }

    [Fact]
    public async Task Leaving_in_progress_by_any_route_releases_the_claim()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await Start(world, "1.1", WriteSeed.Orchestrator);
        var moved = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "review", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.False(moved.Refused, moved.Text);

        await using var db = fixture.NewContext();
        Assert.Equal(0, await db.Claims.CountAsync(c => c.ItemId == world.TaskId && c.ReleasedAt == null, Ct));

        // Released, so the next agent can take it.
        var taken = await Start(world, "1.1", WriteSeed.Other);
        Assert.False(taken.Refused, taken.Text);
    }

    [Fact]
    public async Task An_expired_lease_is_swept_released_recorded_and_the_item_returns_to_ready()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await Start(world, "1.1", WriteSeed.Orchestrator);
        await WriteSeed.ExpireClaimAsync(world, world.TaskId);

        var swept = await WriteSeed.Keeper(fixture).SweepAsync(world.WorkspaceId, Ct);

        Assert.Equal(1, swept);
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.1"));
        Assert.Contains("claim_expired", await WriteSeed.EventKindsAsync(world, world.TaskId));

        await using var db = fixture.NewContext();
        Assert.Equal(0, await db.Claims.CountAsync(c => c.ItemId == world.TaskId && c.ReleasedAt == null, Ct));
    }

    [Fact]
    public async Task A_lapsed_lease_lets_the_next_agent_retake_the_item_in_one_call()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await Start(world, "1.1", WriteSeed.Orchestrator);
        await WriteSeed.ExpireClaimAsync(world, world.TaskId);

        // work_status sweeps before it guards, so the retake is the literal line the expired-claim
        // attention row prints — one call, not "wait and try again".
        var retaken = await Start(world, "1.1", WriteSeed.Other);

        Assert.False(retaken.Refused, retaken.Text);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
    }

    /// <summary>
    /// lease-ready. An unnamed controller's 30-minute lease lapses while a nested agent works for
    /// hours; the sweep returns the item to ready, and done was then refused as "not reachable from
    /// ready". A started item closes from ready under the same five conditions.
    /// </summary>
    [Fact]
    public async Task A_started_item_whose_lease_lapsed_closes_from_ready_and_releases_only_its_own_claim()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "it works", ticked: true);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);

        await Start(world, "1.1", WriteSeed.Orchestrator);
        Assert.False((await Start(world, "9.1", WriteSeed.Other)).Refused);
        await WriteSeed.ExpireClaimAsync(world, world.TaskId);
        await WriteSeed.Keeper(fixture).SweepAsync(world.WorkspaceId, Ct);
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.1"));

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.False(closed.Refused, closed.Text);
        Assert.StartsWith("OK  1.1  ready → done", closed.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));
        Assert.Equal("status", (await WriteSeed.EventKindsAsync(world, world.TaskId))[^1]);

        await using var db = fixture.NewContext();
        var other = await db.Items.SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "9.1", Ct);
        Assert.Equal(1, await db.Claims.CountAsync(c => c.ItemId == other.Id && c.ReleasedAt == null, Ct));
        Assert.Equal(ItemStatus.InProgress, other.Status);
    }

    [Fact]
    public async Task Done_from_ready_is_refused_for_a_never_started_item_even_when_every_condition_holds()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var queued = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Ready);
        await WriteSeed.CriterionAsync(world, queued, 1, "it works", ticked: true);
        await WriteSeed.VerificationAsync(world, queued, VerifyRunner.Orchestrator, command: null);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.2", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("done is not reachable from ready", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.2"));
    }

    [Fact]
    public async Task Done_from_ready_after_a_lapse_still_runs_the_guard()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "it works", ticked: false);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Ready);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("criterion c1", refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("not reachable", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.1"));
    }

    [Fact]
    public async Task A_live_claim_survives_a_sweep()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await Start(world, "1.1", WriteSeed.Orchestrator);

        Assert.Equal(0, await WriteSeed.Keeper(fixture).SweepAsync(world.WorkspaceId, Ct));
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
    }

    private static Task<WriteResult> Start(WriteWorld world, string reference, Actor actor)
        => world.Writer.StatusAsync(world.WorkspaceId, reference, "in_progress", actor, new StatusArgs(), Ct);
}
