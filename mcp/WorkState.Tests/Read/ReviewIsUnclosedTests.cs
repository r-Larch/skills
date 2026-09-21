using WorkState.Core.Domain;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// Adding <c>review_count</c> to <c>item_rollup</c> fixed the two totals, but three other readers
/// of the same buckets still summed only <c>open + doing + blocked</c> and so still treated work in
/// review as if it were not there. Same defect, three more doors.
/// <para>
/// The <c>DoneGuard</c> is deliberately absent from that list, and the reason is the lesson: it
/// asks <c>status NOT IN ('done','cancelled','superseded')</c> — it enumerates the <b>closed</b>
/// set, so a status nobody thought about is unclosed by default and it was never wrong. Every
/// reader that enumerated the <b>open</b> set instead had to be found and fixed by hand.
/// </para>
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ReviewIsUnclosedTests(PostgresFixture fixture)
{
    /// <summary>
    /// doc 06's flag on a parent the store calls closed over children that are not. A child in
    /// review is exactly that disagreement, and the flag used to stay silent for it — the same
    /// laundering the rollup was committing, one layer up.
    /// </summary>
    [Fact]
    public async Task A_done_phase_over_a_child_in_review_still_gets_the_open_children_flag()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        // Seeded directly: the guard would refuse this transition, which is the point — the flag
        // exists to expose the rows that got closed anyway, and the real corpus is full of them.
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Review);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);

        var tree = await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 3, [], true, Ct);

        Assert.Contains("1 open", tree, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart, so the assertion above cannot pass by the flag being unconditional.
    /// </summary>
    [Fact]
    public async Task A_done_phase_over_only_closed_children_gets_no_open_children_flag()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);

        var tree = await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 3, [], true, Ct);

        // "open" also appears as a status word, so match the flag's own shape: "<n> open".
        Assert.DoesNotContain("1 open", tree, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>DefaultScopeAsync</c> ranks runs by how much unclosed work they hold, to pick the scope a
    /// bare <c>work_brief</c> lands on. A run whose only work in flight is in review used to score
    /// zero and sort behind a run with nothing happening at all.
    /// </summary>
    [Fact]
    public async Task A_run_whose_work_is_in_review_outranks_a_run_with_less_unclosed_work()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        // run-a: everything unclosed is in review, and nothing at all is open — including the
        // phases, which are descendants of the run and count toward its score too.
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Review);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Review);
        await WriteSeed.StatusAsync(world, (await world.ItemAsync("2")).Id, ItemStatus.Done);

        // run-b: one open task. Scored 1 before the fix, and therefore won.
        await WriteSeed.SecondRunAsync(world);

        var scope = await new WorkState.Core.Queries.BoardQueries(fixture.DataSource)
            .DefaultScopeAsync(world.WorkspaceId, Ct);

        // Two beats one either way — so this cannot pass on the seq tie-break, only on the count.
        Assert.NotNull(scope);
        Assert.Equal("run-a", scope.Ref);
    }
}
