using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Write;

/// <summary>
/// doc 01 §4 for assumptions: an assumption ref is numbered within its run, so <c>A1</c> exists once
/// per run, and resolving one must address it the way item refs are addressed — qualified, it
/// resolves inside that run; bare and ambiguous, it is refused with both qualified forms.
/// Observed live: <c>work_assume(ref:"ws/A1", resolve:"confirmed")</c> was refused as unknown.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AssumptionRefTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_qualified_assumption_ref_resolves_inside_its_run_only()
    {
        var (world, runA, runB) = await TwoA1sAsync();

        var closed = await Resolve(world, "run-b/A1");

        Assert.False(closed.Refused, closed.Text);
        Assert.Contains("run-b/A1", closed.Text, StringComparison.Ordinal);
        Assert.Equal(AssumptionState.Confirmed, await StateAsync(runB));
        Assert.Equal(AssumptionState.Open, await StateAsync(runA));
    }

    [Fact]
    public async Task A_bare_ref_open_in_two_runs_is_refused_naming_both_qualified_forms()
    {
        var (world, runA, runB) = await TwoA1sAsync();

        var refused = await Resolve(world, "A1");

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("work_assume(ref:\"run-a/A1\"", refused.Text, StringComparison.Ordinal);
        Assert.Contains("work_assume(ref:\"run-b/A1\"", refused.Text, StringComparison.Ordinal);
        Assert.Equal(AssumptionState.Open, await StateAsync(runA));
        Assert.Equal(AssumptionState.Open, await StateAsync(runB));
    }

    [Fact]
    public async Task A_bare_ref_open_in_one_run_still_resolves()
    {
        var (world, runA, runB) = await TwoA1sAsync();
        await Resolve(world, "run-b/A1");

        var closed = await Resolve(world, "A1");

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(AssumptionState.Confirmed, await StateAsync(runA));
    }

    private async Task<(WriteWorld World, Guid RunATask, Guid RunBTask)> TwoA1sAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var runBTask = await WriteSeed.SecondRunAsync(world);

        foreach (var scope in new[] { "run-a/1.1", "run-b/9.1" })
        {
            var opened = await world.Writer.AssumeAsync(
                world.WorkspaceId, WriteSeed.Orchestrator, scope,
                $"an assumption made in {scope}", "reasoned", "medium", null, null, null, Ct);

            Assert.False(opened.Refused, opened.Text);
            Assert.Contains("A1", opened.Text, StringComparison.Ordinal);
        }

        return (world, world.TaskId, runBTask);
    }

    private static Task<WriteResult> Resolve(WriteWorld world, string reference)
        => world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, reference, null, "settled by a probe", null, null,
            "confirmed", null, Ct);

    private async Task<AssumptionState> StateAsync(Guid scopeItemId)
    {
        await using var db = fixture.NewContext();
        return (await db.Assumptions.SingleAsync(a => a.ScopeItemId == scopeItemId, Ct)).State;
    }
}
