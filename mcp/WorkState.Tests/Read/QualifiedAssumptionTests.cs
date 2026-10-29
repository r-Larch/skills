using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>
/// m64 threads-qualified-refs: two runs each hold an open <c>A1</c>, so the bare ref is refused as
/// ambiguous. Every surface that prints a <c>work_assume</c> settle call prints one that, copied as
/// printed, settles the assumption it is about.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class QualifiedAssumptionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task QualifiedAssumption_work_threads_prints_a_call_that_settles_as_printed()
    {
        var world = await TwoA1sAsync();

        var threads = await world.Reader.ThreadsAsync(await world.ScopeAsync("run-a"), ["assumptions"], Ct);

        await SettlesAsync(world, Call(threads), world.TaskId);
    }

    [Fact]
    public async Task QualifiedAssumption_attention_prints_a_call_that_settles_as_printed()
    {
        var world = await TwoA1sAsync();
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);

        var lines = await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct);
        var due = Assert.Single(lines, l => l.Rank == Core.Rendering.AttentionRank.AssumptionPastRevisit);

        await SettlesAsync(world, Call(due.Call), world.TaskId);
    }

    [Fact]
    public async Task QualifiedAssumption_work_report_prints_a_call_that_settles_as_printed()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var runBTask = await WriteSeed.SecondRunAsync(world);
        await OpenAsync(world, "run-a/1.1", null);

        var reported = await world.Writer.ReportAsync(
            world.WorkspaceId,
            "run-b/9.1",
            WriteSeed.Worker,
            new ReportArgs("partial", Assumptions: ["an assumption made in run-b/9.1"]),
            Ct);

        Assert.False(reported.Refused, reported.Text);
        await SettlesAsync(world, Call(reported.Text), runBTask);
    }

    /// <summary>run-a's A1 revisits when 1.1 closes; run-b holds an A1 of its own.</summary>
    private async Task<WriteWorld> TwoA1sAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        await OpenAsync(world, "run-a/1.1", "run-a/1.1");
        await OpenAsync(world, "run-b/9.1", null);
        return world;
    }

    private static async Task OpenAsync(WriteWorld world, string scope, string? revisitWhen)
    {
        var opened = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, scope,
            $"an assumption made in {scope}", "reasoned", "medium", revisitWhen, null, null, Ct);

        Assert.False(opened.Refused, opened.Text);
        Assert.Contains("A1", opened.Text, StringComparison.Ordinal);
    }

    private static string Call(string text)
    {
        var match = SettleCall().Match(text);
        Assert.True(match.Success, text);
        return match.Value;
    }

    /// <summary>Runs <paramref name="call"/> with the arguments it prints, and checks it closed the assumption on <paramref name="scopeItemId"/>.</summary>
    private async Task SettlesAsync(WriteWorld world, string call, Guid scopeItemId)
    {
        var printed = SettleCall().Match(call);

        var settled = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, printed.Groups["ref"].Value, null,
            "settled by the printed call", null, null, printed.Groups["resolve"].Value, null, Ct);

        Assert.False(settled.Refused, $"{call}\n{settled.Text}");

        await using var db = fixture.NewContext();
        var assumption = await db.Assumptions.SingleAsync(a => a.ScopeItemId == scopeItemId, Ct);
        Assert.Equal(AssumptionState.Confirmed, assumption.State);
    }

    [GeneratedRegex("""work_assume\(ref:"(?<ref>[^"]+)", resolve:"(?<resolve>[a-z]+)", evidence:"…"\)""")]
    private static partial Regex SettleCall();
}
