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

    /// <summary>assumption-reach. work_get(refs:["A1"]) answered "no item has ref A1".</summary>
    [Fact]
    public async Task Work_get_prints_an_assumption_whole_by_its_ref()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var statement = string.Join(" ", Enumerable.Repeat("the export endpoint answers inside the corporate network", 3));
        await AssumeAsync(world, world.RunId, "A1", statement, revisit: world.TaskId);

        var got = await world.Reader.GetAsync(await world.ScopeAsync("run-a"), ["A1"], [], Ct);
        var joined = System.Text.RegularExpressions.Regex.Replace(got, @"\n {12}", " ");

        Assert.StartsWith("A1      assum   open       run-a/A1\n", got, StringComparison.Ordinal);
        Assert.Contains($"  statement {statement}\n", joined, StringComparison.Ordinal);
        Assert.Contains("  evidence  reasoned — the probe answered from the dev box\n", joined, StringComparison.Ordinal);
        Assert.Contains("  reversal  medium\n", got, StringComparison.Ordinal);
        Assert.Contains("  revisit   when 1.1 closes (it is open)\n", got, StringComparison.Ordinal);
        Assert.Contains("  scope     run-a\n", got, StringComparison.Ordinal);
        Assert.EndsWith(
            "Next: work_assume(ref:\"run-a/A1\", resolve:\"confirmed\", evidence:\"…\") once it is settled — or disproven, obsolete.",
            got,
            StringComparison.Ordinal);
        Assert.DoesNotContain("no item", got, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_assumption_ref_in_two_runs_is_refused_with_the_qualified_forms_and_each_reads_alone()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world);
        Guid runB;
        await using (var db = fixture.NewContext())
        {
            runB = db.Items.Single(i => i.WorkspaceId == world.WorkspaceId && i.Ref == "run-b").Id;
        }

        await AssumeAsync(world, world.RunId, "A1", "Run a's assumption.");
        await AssumeAsync(world, runB, "A1", "Run b's assumption.");
        var home = await world.ScopeAsync("run-a");

        var ambiguous = await world.Reader.GetAsync(home, ["A1"], [], Ct);
        var qualified = await world.Reader.GetAsync(home, ["run-b/A1", "1.1"], [], Ct);

        Assert.Equal(
            "✗ \"A1\" names 2 assumptions across runs — qualify it → work_get(refs:[\"run-a/A1\", \"run-b/A1\"])",
            ambiguous);
        Assert.Contains("  statement Run b's assumption.", qualified, StringComparison.Ordinal);
        Assert.DoesNotContain("Run a's assumption.", qualified, StringComparison.Ordinal);
        Assert.Contains("The task under test", qualified, StringComparison.Ordinal);
    }

    /// <summary>assumption-reach. The project brief showed none of the project's open assumptions.</summary>
    [Fact]
    public async Task The_project_brief_lists_open_assumptions_whole_capped_with_the_call_that_lists_all()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        for (var n = 1; n <= 7; n++)
        {
            await AssumeAsync(world, world.RunId, $"A{n}", $"Assumption number {n} still holds.");
        }

        string project;
        await using (var db = fixture.NewContext())
        {
            project = db.Items.Single(i => i.Id == world.ProjectId).Ref;
        }

        var lines = (await world.Reader.BriefAsync(await world.ScopeAsync(project), "resume", Ct)).Split('\n');

        Assert.Contains("OPEN ASSUMPTIONS (7)", lines);
        Assert.Contains(lines, l => l.StartsWith("  run-a/A1 Assumption number 1 still holds.", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Assumption number 6", StringComparison.Ordinal));
        Assert.Contains($"  · +2 more — work_threads(scope:\"{project}\", include:\"assumptions\")", lines);
    }

    private async Task AssumeAsync(WriteWorld world, Guid scope, string reference, string statement, Guid? revisit = null)
    {
        await using var db = fixture.NewContext();
        db.Assumptions.Add(new Assumption
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = world.WorkspaceId,
            ScopeItemId = scope,
            Ref = reference,
            Statement = statement,
            Evidence = EvidenceLevel.Reasoned,
            EvidenceNote = revisit is null ? null : "the probe answered from the dev box",
            Reversal = ReversalCost.Medium,
            RevisitItemId = revisit,
            State = AssumptionState.Open,
        });
        await db.SaveChangesAsync(Ct);
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
