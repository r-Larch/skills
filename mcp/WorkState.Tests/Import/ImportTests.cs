using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Import;

/// <summary>
/// The importer against real orchestration files. Every assertion here is about a shape the corpus
/// actually writes (RECON-CORPUS), not one invented for the test.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ImportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_plan_becomes_a_tree_whose_frontier_offers_the_first_task()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run");
        var queries = new WorkStateQueries(fixture.DataSource);

        var phase = await run.RequireItemAsync("phase-1");
        Assert.Equal("phase", phase.Kind);

        var first = await run.RequireItemAsync("T1.1");
        Assert.Equal(phase.Id, first.ParentId);
        Assert.Equal(ItemStatus.Open, first.Status);
        Assert.Equal("dotnet build Nomos.slnx", first.VerifyCmd);

        var frontier = await queries.ReadyFrontierAsync(phase.Id, Ct);

        Assert.Equal("T1.1", frontier[0].Ref);
        Assert.Contains(frontier, r => r.Ref == "T1.2");

        // A phase with open children is not itself actionable, which is what makes the frontier an
        // answer to "what next" rather than a list of everything open.
        var runFrontier = await queries.ReadyFrontierAsync(run.RunItemId, Ct);
        Assert.DoesNotContain(runFrontier, r => r.Ref == "phase-1");
    }

    [Fact]
    public async Task A_plan_whose_tasks_are_checkboxes_still_reaches_the_tree_with_its_criteria()
    {
        var run = await Corpus.ImportAsync(fixture, "checklist-run");

        var open = await run.RequireItemAsync("1.1");
        Assert.Equal(ItemStatus.Open, open.Status);
        Assert.Equal(
            "dotnet test Tests/Tests.csproj --filter FullyQualifiedName~Guard",
            open.VerifyCmd);

        await using var db = run.NewContext();
        var criteria = await db.ItemCriteria.Where(c => c.ItemId == open.Id).ToListAsync(Ct);
        Assert.Contains(criteria, c => c.Text.Contains("fails the guard", StringComparison.Ordinal));

        // A ticked box is a closed task; that is the whole status vocabulary a checklist has.
        var done = await run.RequireItemAsync("1.3");
        Assert.Equal(ItemStatus.Done, done.Status);
        Assert.Equal(ItemResolution.Accepted, done.Resolution);
        Assert.NotNull(done.ClosedAt);

        var phase = await run.RequireItemAsync("phase-2");
        var phaseCriteria = await db.ItemCriteria.Where(c => c.ItemId == phase.Id).ToListAsync(Ct);
        Assert.Contains(phaseCriteria, c => c.Text.Contains("blocks verbatim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_assumption_table_parses_with_its_evidence_and_reversal()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run");

        await using var db = run.NewContext();
        var assumptions = await db.Assumptions
            .Where(a => a.ScopeItemId == run.RunItemId)
            .OrderBy(a => a.Ref)
            .ToListAsync(Ct);

        Assert.Equal(["A1", "A2", "A3", "A4"], assumptions.Select(a => a.Ref));

        var a2 = assumptions.Single(a => a.Ref == "A2");
        Assert.Equal(EvidenceLevel.Reasoned, a2.Evidence);
        Assert.Equal(ReversalCost.Low, a2.Reversal);
        Assert.Equal(AssumptionState.Open, a2.State);
        Assert.StartsWith("Materialising files.content", a2.Statement);

        // A struck-through row is a closed assumption, which is how the corpus retires one.
        var a1 = assumptions.Single(a => a.Ref == "A1");
        Assert.Equal(AssumptionState.Confirmed, a1.State);
        Assert.NotNull(a1.ResolvedAt);
    }

    [Fact]
    public async Task A_task_log_written_as_a_list_still_yields_its_verdicts_and_commits()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run");

        var configDoc = await run.RequireItemAsync("3.1");
        Assert.Equal(ItemStatus.Done, configDoc.Status);
        Assert.Equal(ItemResolution.Accepted, configDoc.Resolution);

        // The list form is the only reader that records the line itself, so this is its fingerprint.
        Assert.Contains("config doc", configDoc.Intent ?? "", StringComparison.Ordinal);

        var windows = await run.RequireItemAsync("1.3");

        await using var db = run.NewContext();
        var commits = await db.Artifacts
            .Where(a => a.ItemId == windows.Id && a.Kind == "commit")
            .Select(a => a.Value)
            .ToListAsync(Ct);

        Assert.Contains("4a96f3165", commits);
    }

    [Fact]
    public async Task A_brief_and_the_ledger_row_that_closed_it_are_one_item()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run");

        var task = await run.RequireItemAsync("1.1");

        await using var db = run.NewContext();
        var attempt = await db.Attempts.SingleAsync(a => a.ItemId == task.Id, Ct);
        var brief = await db.Docs.SingleAsync(d => d.Id == attempt.BriefDocId, Ct);

        Assert.Equal("brief-1.1.md", brief.Slug);
        Assert.Equal(1, attempt.N);

        // The ledger's own row for the same handle closed it: one ref, one item, one lifecycle.
        Assert.Equal(ItemStatus.Done, task.Status);
    }

    [Fact]
    public async Task Every_file_becomes_a_doc_whose_blocks_are_verbatim_and_still_utf8()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run");

        await using var db = run.NewContext();
        var slugs = await db.Docs
            .Where(d => d.WorkspaceId == run.WorkspaceId)
            .Select(d => d.Slug)
            .OrderBy(s => s)
            .ToListAsync(Ct);

        Assert.Equal(
            ["brief-1.1.md", "conventions.md", "ledger-rich.md", "ledger-small.md", "plan.md"],
            slugs);

        var source = await File.ReadAllTextAsync(
            Path.Combine(Corpus.Fixture("sample-run"), "LEDGER-small.md"),
            Ct);
        var normalized = source.Replace("\r\n", "\n");

        var blocks = await run.BlocksAsync("ledger-small.md");
        Assert.NotEmpty(blocks);

        foreach (var block in blocks)
        {
            Assert.Contains(block.Body, normalized, StringComparison.Ordinal);
            Assert.DoesNotContain('�', block.Body);
        }

        Assert.Contains(
            blocks,
            b => b.Heading is not null && b.Heading.Contains("🚨", StringComparison.Ordinal));
        Assert.Equal(0, run.Outcome.ReplacementChars);
    }

    [Fact]
    public async Task Carried_forward_sections_become_notes_the_next_brief_can_consume()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run");

        await using var db = run.NewContext();
        var notes = await db.Notes
            .Where(n => n.WorkspaceId == run.WorkspaceId && n.Kind == NoteKind.CarryForward)
            .ToListAsync(Ct);

        // Two "Carry into later briefs" sections in one ledger — the outbox is written more than once.
        Assert.True(notes.Count >= 10, $"expected the carry-forward outbox, got {notes.Count} notes");
        Assert.Contains(notes, n => n.Body.Contains("ToolOutputBudget.PartialNotice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conventions_become_constraint_rules_scoped_to_the_run()
    {
        var run = await Corpus.ImportAsync(fixture, "sample-run");

        await using var db = run.NewContext();
        var rules = await db.ConstraintRules
            .Where(c => c.ScopeItemId == run.RunItemId)
            .ToListAsync(Ct);

        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.Kind == ConstraintKind.RedLine);
    }

    [Fact]
    public async Task A_second_import_of_the_same_run_updates_in_place()
    {
        var slug = $"imp-twice-{Guid.CreateVersion7():n}";

        var once = await Corpus.ImportAsync(fixture, "sample-run", slug);
        var afterFirst = await Corpus.CensusAsync(once);

        var twice = await Corpus.ImportAsync(fixture, "sample-run", slug);
        var afterSecond = await Corpus.CensusAsync(twice);

        Assert.Equal(once.RunItemId, twice.RunItemId);
        Assert.Equal(afterFirst, afterSecond);
        Assert.True(afterFirst["item"] > 20, $"the fixture run should be substantial, got {afterFirst["item"]} items");
    }
}
