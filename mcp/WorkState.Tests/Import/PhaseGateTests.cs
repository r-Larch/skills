using Microsoft.EntityFrameworkCore;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Import.Extraction;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Import;

/// <summary>
/// FINDINGS-live F-L1, F-L1b and F-L2 — the three things the whole Gaia corpus found that 91 passing
/// fixture tests could not. Every assertion is paired with its permissive twin: a rule that makes
/// every phase <c>review</c>, or fuses every list into one rule, passes the refusal half on its own.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class PhaseGateTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_phase_whose_ledger_records_a_passed_gate_imports_as_done()
    {
        var run = await Corpus.ImportAsync(fixture, "gate-run");

        var phase = await run.RequireItemAsync("phase-1");

        Assert.Equal(ItemStatus.Done, phase.Status);
        Assert.Equal(ItemResolution.Accepted, phase.Resolution);
        Assert.NotNull(phase.ClosedAt);

        // The heading carries both outcomes — "FAILED, then PASSED after rework" — and the later one
        // is the operative one, so reading the first token would close this phase as a failure.
        await using var db = run.NewContext();
        var judged = await db.Events
            .Where(e => e.ItemId == phase.Id && e.Kind == "judged")
            .Select(e => e.Summary)
            .ToListAsync(Ct);

        Assert.Contains("phase-1: GATE PASSED", judged);
    }

    [Fact]
    public async Task A_phase_whose_tasks_are_all_done_with_no_gate_line_imports_as_review_not_done()
    {
        var run = await Corpus.ImportAsync(fixture, "gate-run");

        var phase = await run.RequireItemAsync("phase-2");

        // The whole argument of doc 07 §2: finished work is ready to be gated, and deriving `done`
        // here would delete the gate — the composition review that catches the seams.
        Assert.Equal(ItemStatus.Review, phase.Status);
        Assert.Null(phase.Resolution);
        Assert.Null(phase.ClosedAt);

        await using var db = run.NewContext();
        var children = await db.Items.Where(i => i.ParentId == phase.Id).ToListAsync(Ct);
        Assert.Equal(3, children.Count);
        Assert.All(children, c => Assert.Equal(ItemStatus.Done, c.Status));
    }

    [Fact]
    public async Task A_phase_with_an_open_child_still_imports_as_open()
    {
        var run = await Corpus.ImportAsync(fixture, "gate-run");

        var phase = await run.RequireItemAsync("phase-3");

        Assert.Equal(ItemStatus.Open, phase.Status);
        Assert.Equal(ItemStatus.Open, (await run.RequireItemAsync("3.2")).Status);
        Assert.Equal(ItemStatus.Done, (await run.RequireItemAsync("3.1")).Status);
    }

    [Theory]
    [InlineData("Phase 2 gate — FAILED, then PASSED after rework", ItemStatus.Done)]
    [InlineData("PHASE 4 GATE — PASSES WITH FINDINGS (last SHA `64f23fdb`)", ItemStatus.Done)]
    [InlineData("⚠⚠ PHASE 7 GATE — FAIL", ItemStatus.Review)]
    [InlineData("Phase 6 — CLOSED. Gate criterion met.", ItemStatus.Done)]
    public void A_gate_heading_maps_to_the_status_its_last_outcome_token_states(string heading, ItemStatus expected)
        => Assert.Equal(expected, Vocabulary.ReadGateOutcome(heading)?.Status);

    [Theory]
    [InlineData("Phase 2 briefing inputs (from the Phase 1 gate — fold these into the 2.x briefs)")]
    [InlineData("PHASE 4 TAIL RE-GATE — ran, found a BLOCKER, fixed as 4.6b")]
    [InlineData("Phase 6 gate — composition review")]
    [InlineData("Phase 3 — COMPLETE. Measured against live DSS data.")]
    public void A_heading_that_records_no_gate_outcome_leaves_the_phase_alone(string heading)
        => Assert.Null(Vocabulary.ReadGateOutcome(heading));

    [Fact]
    public async Task The_attention_block_names_the_ungated_phase_and_leaves_the_unfinished_one_alone()
    {
        var run = await Corpus.ImportAsync(fixture, "gate-run");
        var reader = new BoardReader(
            new WorkStateQueries(fixture.DataSource),
            new BoardQueries(fixture.DataSource));

        var scope = Assert.IsType<ScopeResult.Found>(
            await reader.ResolveAsync(run.WorkspaceSlug, "gate-run", Ct));

        var lines = await reader.AttentionAsync(scope, Ct);
        var gates = lines.Where(l => l.Rank == AttentionRank.PhaseGateNotRun).ToList();

        var only = Assert.Single(gates);
        Assert.Equal("phase-2: all 3 tasks done, gate not run", only.What);
        Assert.Contains("work_brief(scope:\"phase-2\", for:\"gate\")", only.Call, StringComparison.Ordinal);

        // Nothing transitioned: the phase is still whatever the importer said it was, and the line is
        // the only thing that moved (FINDINGS-live F-L1b).
        Assert.Equal(ItemStatus.Review, (await run.RequireItemAsync("phase-2")).Status);
    }

    [Fact]
    public async Task A_multi_line_red_line_imports_as_one_rule_with_its_verb_intact()
    {
        var run = await Corpus.ImportAsync(fixture, "gate-run");

        await using var db = run.NewContext();
        var redLines = await db.ConstraintRules
            .Where(c => c.ScopeItemId == run.RunItemId && c.Kind == ConstraintKind.RedLine)
            .Select(c => c.Text)
            .ToListAsync(Ct);

        var invariant = Assert.Single(redLines, t => t.Contains("App.Web/Core/Rag", StringComparison.Ordinal));

        Assert.Contains("never modify", invariant, StringComparison.Ordinal);
        Assert.Contains("App.Web/Features/DataQueries", invariant, StringComparison.Ordinal);
        Assert.Contains("ChatbotResponder", invariant, StringComparison.Ordinal);

        // The permissive twin: a list of sentences is still one rule per sentence, so the fix cannot
        // have collapsed every convention list into a single paragraph.
        Assert.Contains(redLines, t => t.StartsWith("Never connect to", StringComparison.Ordinal));
        Assert.Contains(redLines, t => t.StartsWith("Always pass", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rule_the_conventions_file_no_longer_states_is_retracted_by_the_next_import()
    {
        var slug = $"gate-retract-{Guid.CreateVersion7():n}";
        var run = await Corpus.ImportAsync(fixture, "gate-run", slug);

        await using (var db = run.NewContext())
        {
            db.ConstraintRules.Add(new ConstraintRule
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = run.WorkspaceId,
                ScopeItemId = run.RunItemId,
                Ref = "C900",
                Kind = ConstraintKind.RedLine,
                Text = "App.Web/Core/Rag/",
            });

            await db.SaveChangesAsync(Ct);
        }

        await Corpus.ImportAsync(fixture, "gate-run", slug);

        await using var after = run.NewContext();
        var rules = await after.ConstraintRules
            .Where(c => c.ScopeItemId == run.RunItemId)
            .ToListAsync(Ct);

        Assert.False(rules.Single(r => r.Text == "App.Web/Core/Rag/").Active);
        Assert.All(rules.Where(r => r.Text != "App.Web/Core/Rag/"), r => Assert.True(r.Active));
    }

    [Fact]
    public async Task A_second_import_of_the_gate_run_changes_no_count()
    {
        var slug = $"gate-twice-{Guid.CreateVersion7():n}";

        var once = await Corpus.ImportAsync(fixture, "gate-run", slug);
        var afterFirst = await Corpus.CensusAsync(once);

        var twice = await Corpus.ImportAsync(fixture, "gate-run", slug);
        var afterSecond = await Corpus.CensusAsync(twice);

        Assert.Equal(once.RunItemId, twice.RunItemId);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(ItemStatus.Review, (await twice.RequireItemAsync("phase-2")).Status);
    }
}
