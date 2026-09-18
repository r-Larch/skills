using Microsoft.EntityFrameworkCore;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Import;

/// <summary>
/// What the importer cannot type. The rule is the same for every shape here: it lands in a
/// <c>doc_block</c> verbatim, it is counted in the report, and it does not stop the run — the
/// parse-failure list is the deliverable as much as the rows are (brief §3).
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ImportUnmappedTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_malformed_table_lands_in_a_block_and_is_counted_not_lost()
    {
        var run = await Corpus.ImportAsync(fixture, "malformed-run");

        Assert.True(
            run.Unmapped("malformed-row") >= 2,
            $"both arity-broken rows should be counted, got {run.Unmapped("malformed-row")}");

        var blocks = await run.BlocksAsync("ledger.md");
        Assert.Contains(
            blocks,
            b => b.Body.Contains("the row with a missing cell", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_whose_header_was_deleted_is_kept_and_counted_as_an_orphan()
    {
        var run = await Corpus.ImportAsync(fixture, "malformed-run");

        Assert.Equal(2, run.Unmapped("pipe-row-belonging-to-no-table"));

        var blocks = await run.BlocksAsync("ledger.md");
        Assert.Contains(blocks, b => b.Body.Contains("a row whose header was deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_grid_that_is_prose_is_reported_by_its_signature_rather_than_guessed_at()
    {
        var run = await Corpus.ImportAsync(fixture, "malformed-run");

        Assert.Contains(
            run.Outcome.Unmapped.Shapes,
            s => s.Category == "table" && s.Shape == "| environment | value |");
    }

    [Fact]
    public async Task An_evidence_word_the_vocabulary_does_not_know_is_reported_not_invented()
    {
        var run = await Corpus.ImportAsync(fixture, "malformed-run");

        Assert.Contains(
            run.Outcome.Unmapped.Shapes,
            s => s.Category == "assumption-evidence-token" && s.Shape == "vibes");
    }

    [Fact]
    public async Task A_spec_is_referenced_by_path_and_sha_and_its_text_never_enters_the_store()
    {
        var run = await Corpus.ImportAsync(fixture, "malformed-run");

        await using var db = run.NewContext();

        Assert.Empty(await db.Docs.Where(d => d.WorkspaceId == run.WorkspaceId && d.Slug == "spec.md").ToListAsync(Ct));

        var artifacts = await db.Artifacts
            .Where(a => a.ItemId == run.RunItemId)
            .ToListAsync(Ct);

        var spec = Assert.Single(artifacts, a => a.Value == "SPEC.md");
        Assert.Contains("sha256", spec.Meta ?? "", StringComparison.Ordinal);

        // Measurement output is a path too (owner decision D2), never content.
        Assert.Single(artifacts, a => a.Value == "baseline/suite.log" && a.Kind == "log");

        var docIds = await db.Docs.Where(d => d.WorkspaceId == run.WorkspaceId).Select(d => d.Id).ToListAsync(Ct);
        Assert.Empty(await db.DocBlocks
            .Where(b => docIds.Contains(b.DocId) && b.Body.Contains("the trap closed on us"))
            .ToListAsync(Ct));
    }
}
