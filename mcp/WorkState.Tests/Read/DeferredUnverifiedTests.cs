using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Tests.App;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// defer-verify. A deferral closes the item <c>done</c> with resolution <c>obsolete</c>: the work
/// moved, it did not finish. Attention then said "1.1 is done with no orchestrator verification",
/// which invites a false verification. Every surface that counts done-unverified leaves a moved item
/// out: attention, the audit, the <c>⚠ no verify</c> flag and the honesty strip. A done item that
/// really was closed unverified still counts on all four.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class DeferredUnverifiedTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_deferred_item_is_not_done_unverified_on_any_surface_and_an_unverified_done_one_still_is()
    {
        var world = await DeferredAndUnverifiedAsync();
        var scope = await world.ScopeAsync("run-a");

        var attention = await world.Reader.AttentionAsync(scope, Ct);
        Assert.Equal(
            "1.2",
            string.Join(", ", attention.Where(l => l.Rank == AttentionRank.DoneUnverified).Select(l => l.Ref)));

        var audit = await world.Reader.AuditAsync(scope, Ct);
        Assert.Contains("✗ 1.2", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("✗ 1.1", audit, StringComparison.Ordinal);

        var deferred = await world.Reader.GetAsync(scope, ["1.1"], [], Ct);
        Assert.DoesNotContain(Flag.NoVerify, deferred, StringComparison.Ordinal);

        var unverified = await world.Reader.GetAsync(scope, ["1.2"], [], Ct);
        Assert.Contains(Flag.NoVerify, unverified, StringComparison.Ordinal);

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var client = await server.SignInAsync(Ct);
        var watch = await AppServer.ReadJsonAsync(client, "/api/watch", Ct);

        Assert.Equal(1, watch.GetProperty("honesty").GetProperty("doneUnverified").GetInt32());
    }

    /// <summary>1.1 deferred to the backlog through work_status; 1.2 done with no verification at all.</summary>
    private async Task<WriteWorld> DeferredAndUnverifiedAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);

        var moved = await world.Writer.StatusAsync(
            world.WorkspaceId,
            "1.1",
            "deferred",
            WriteSeed.Orchestrator,
            new StatusArgs(Reason: "belongs in the next run", Target: "backlog"),
            Ct);

        Assert.False(moved.Refused, moved.Text);

        var item = await world.ItemAsync("1.1");
        Assert.Equal(ItemStatus.Done, item.Status);
        Assert.Equal(ItemResolution.Obsolete, item.Resolution);

        return world;
    }
}
