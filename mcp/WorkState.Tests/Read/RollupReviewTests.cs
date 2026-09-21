using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// <c>review</c> is unclosed work in flight, and <c>item_rollup</c> had no bucket for it — so it
/// appeared in neither half of any "X of Y" and simply vanished. The corpus has the shape:
/// <c>phase-4</c> of <c>entity-store</c> has 23 children — 10 done, 12 open, 1 in review — and the
/// tree reported 10 of 22. doc 09 §5 makes that phase the worked example of the disagreement this
/// product exists to surface, so a denominator that drops work in review is the same laundering,
/// committed by the tool doing the accusing.
/// <para>
/// <c>cancelled</c> and <c>superseded</c> stay out of every bucket on purpose: they are closed and
/// were not done, so they belong in neither the numerator nor the denominator.
/// </para>
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RollupReviewTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_denominator_counts_a_child_in_review()
    {
        var world = await OneOfEachAsync();

        var rollup = await Queries().RollupAsync(world.PhaseId, Ct);

        Assert.NotNull(rollup);
        Assert.Equal(5, Total(rollup));
    }

    /// <summary>
    /// The over-correction guard. A fix that counted every child regardless of status would satisfy
    /// <see cref="The_denominator_counts_a_child_in_review"/> and break this one.
    /// </summary>
    [Fact]
    public async Task A_cancelled_or_superseded_child_leaves_the_denominator_where_it_was()
    {
        var world = await OneOfEachAsync();
        var queries = Queries();

        var before = Total(await queries.RollupAsync(world.PhaseId, Ct));
        Assert.Equal(5, before);

        await WriteSeed.ChildAsync(world, world.PhaseId, "1.6", status: ItemStatus.Cancelled);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.7", status: ItemStatus.Superseded);

        Assert.Equal(before, Total(await queries.RollupAsync(world.PhaseId, Ct)));
    }

    [Fact]
    public async Task Review_is_its_own_bucket_and_is_folded_into_neither_open_nor_doing()
    {
        var world = await OneOfEachAsync();

        var rollup = await Queries().RollupAsync(world.PhaseId, Ct);

        Assert.NotNull(rollup);
        Assert.Equal(1, rollup.ReviewCount);
        Assert.Equal(1, rollup.OpenCount);
        Assert.Equal(1, rollup.DoingCount);
        Assert.Equal(1, rollup.BlockedCount);
        Assert.Equal(1, rollup.DoneCount);
    }

    /// <summary>
    /// A phase whose only unclosed children are in review matched neither half of the old predicate,
    /// so <c>WHERE phase N of M</c> skipped it and pointed the reader at a later one.
    /// </summary>
    [Fact]
    public async Task A_phase_whose_only_unclosed_child_is_in_review_is_the_current_phase()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Review);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);

        // Phase 2 is work not started, and used to win purely because review matched nothing.
        var second = await world.ItemAsync("2");
        await WriteSeed.ChildAsync(world, second.Id, "2.1");

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("run-a"), "resume", Ct);

        Assert.Contains("phase 1 of 2", brief, StringComparison.Ordinal);
    }

    /// <summary>One child in each of the five statuses an "X of Y" is entitled to count.</summary>
    private async Task<WriteWorld> OneOfEachAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        // Task 1.1 is seeded open.
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.InProgress);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.3", status: ItemStatus.Blocked);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.4", status: ItemStatus.Review);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.5", status: ItemStatus.Done);

        return world;
    }

    private static long Total(ItemRollup? rollup)
        => rollup is null
            ? 0
            : rollup.OpenCount + rollup.DoingCount + rollup.BlockedCount
              + rollup.ReviewCount + rollup.DoneCount;

    private WorkStateQueries Queries() => new(fixture.DataSource);
}
