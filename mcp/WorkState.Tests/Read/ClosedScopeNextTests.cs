using WorkState.Core.Domain;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// donenext. <c>work_brief(scope:"autowork/m1")</c> said <c>NEXT work_status(ref:"m1", to:"done")</c>
/// while m1 was done, and the call it named was refused "m1 is done". With no child open, the NEXT
/// for a closed scope names the open work derived from it, or says that nothing is left.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ClosedScopeNextTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(ItemStatus.Done, "done")]
    [InlineData(ItemStatus.Cancelled, "cancelled")]
    public async Task A_closed_run_with_every_child_closed_says_nothing_is_left(ItemStatus closed, string word)
    {
        var world = await ClosedRunAsync(closed);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("run-a"), "resume", Ct);

        Assert.DoesNotContain("work_status(ref:\"run-a\"", brief, StringComparison.Ordinal);
        Assert.Equal($"NEXT   nothing left — run-a is {word}", NextLine(brief));
    }

    [Fact]
    public async Task A_closed_run_names_the_open_work_derived_from_it_wherever_it_sits()
    {
        var world = await ClosedRunAsync(ItemStatus.Done);
        var backlog = await WriteSeed.SecondRunAsync(world, "backlog");
        await WriteSeed.LinkAsync(world, backlog, world.TaskId, LinkType.DerivedFrom);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("run-a"), "resume", Ct);

        Assert.DoesNotContain("work_status(ref:\"run-a\"", brief, StringComparison.Ordinal);
        Assert.Equal(
            "NEXT   work_get(refs:[\"9.1\"]) — run-a is done; 9.1 derived from it is still open",
            NextLine(brief));
    }

    [Fact]
    public async Task A_run_in_review_with_every_child_closed_still_proposes_closing_it()
    {
        var world = await ClosedRunAsync(ItemStatus.Review);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("run-a"), "resume", Ct);

        Assert.Equal("NEXT   work_status(ref:\"run-a\", to:\"done\") — every child is closed", NextLine(brief));
    }

    private static string NextLine(string brief)
        => brief.Split('\n').Single(l => l.StartsWith("NEXT", StringComparison.Ordinal));

    /// <summary>run-a with every child done, and run-a itself in <paramref name="status"/>.</summary>
    private async Task<WriteWorld> ClosedRunAsync(ItemStatus status)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, (await world.ItemAsync("2")).Id, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.RunId, status);
        return world;
    }
}
