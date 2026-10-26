using WorkState.Core.Domain;
using WorkState.Core.Rendering;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Read;

/// <summary>papercuts-2 — the read-side follow-ups found 2026-10-26.</summary>
[Collection(StoreCollection.Name)]
public sealed class PapercutTests(PostgresFixture fixture)
{
    /// <summary>tree-status-filter. The filter compared "inprogress", so the spelling work_status takes matched nothing.</summary>
    [Theory]
    [InlineData("in_progress")]
    [InlineData("inprogress")]
    [InlineData("IN_PROGRESS")]
    public async Task Work_tree_keeps_in_progress_items_under_either_spelling(string spelling)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        var home = await world.ScopeAsync("run-a");

        var tree = await world.Reader.TreeAsync(home, 3, [spelling.ToLowerInvariant()], true, Ct);

        Assert.Contains("1.1", tree, StringComparison.Ordinal);
        Assert.Contains("The task under test", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("Ingestion", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_tree_refuses_an_unknown_status_naming_the_valid_ones()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var home = await world.ScopeAsync("run-a");

        var tree = await world.Reader.TreeAsync(home, 3, ["open", "doing"], true, Ct);

        Assert.StartsWith("REFUSED", tree, StringComparison.Ordinal);
        Assert.Contains("\"doing\" is not a status", tree, StringComparison.Ordinal);
        Assert.Contains("open, ready, in_progress, blocked, review, done, cancelled, superseded", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("Foundations", tree, StringComparison.Ordinal);
    }

    /// <summary>now-ago. The age glyph says "now" and every caller appended " ago".</summary>
    [Fact]
    public void A_moment_under_a_minute_old_reads_just_now()
    {
        Assert.Equal("just now", Glyph.Ago(TimeSpan.FromSeconds(20)));
        Assert.Equal("5m ago", Glyph.Ago(TimeSpan.FromMinutes(5)));
        Assert.Equal("2h ago", Glyph.Ago(TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task Work_get_and_the_brief_print_a_fresh_verification_as_just_now()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        var home = await world.ScopeAsync("run-a");

        var got = await world.Reader.GetAsync(home, ["1.1"], [], Ct);
        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "resume", Ct);

        Assert.Contains("by orchestrator, just now", got, StringComparison.Ordinal);
        Assert.Contains("by orchestrator, just now", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("now ago", got + brief, StringComparison.Ordinal);
    }

    /// <summary>attention-cap. Two wrapped assumptions printed "8 unclosed" — the header counted lines.</summary>
    [Fact]
    public async Task The_threads_header_counts_the_rows_it_lists_not_their_wrapped_lines()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using (var db = fixture.NewContext())
        {
            foreach (var n in new[] { 1, 2 })
            {
                db.Assumptions.Add(new Assumption
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = world.WorkspaceId,
                    ScopeItemId = world.RunId,
                    Ref = $"A{n}",
                    Statement = string.Join(" ", Enumerable.Repeat($"assumption {n} holds because the export endpoint answers", 4)),
                    Evidence = EvidenceLevel.Reasoned,
                    Reversal = ReversalCost.Medium,
                    State = AssumptionState.Open,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        var threads = await world.Reader.ThreadsAsync(await world.ScopeAsync("run-a"), ["assumptions"], Ct);

        Assert.StartsWith("THREADS  2 unclosed under run-a\n", threads, StringComparison.Ordinal);
        Assert.True(threads.Split('\n').Length > 4, threads);
    }
}
