using Microsoft.EntityFrameworkCore;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Import;
using WorkState.Tests.Store;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Import;

/// <summary>
/// One import of one fixture directory into a workspace of its own. The workspace is per-test so two
/// tests cannot see each other's rows, and the fixtures are copies committed into the repo: the live
/// corpora are other projects' working records and they change under us.
/// </summary>
internal sealed record ImportedRun(
    PostgresFixture Fixture,
    string WorkspaceSlug,
    Guid WorkspaceId,
    Guid RunItemId,
    ImportOutcome Outcome)
{
    public WorkStateDbContext NewContext() => Fixture.NewContext();

    public async Task<Item?> ItemAsync(string reference)
    {
        await using var db = NewContext();
        return await db.Items.FirstOrDefaultAsync(
            i => i.RefScopeId == RunItemId && i.Ref == reference,
            Ct);
    }

    public async Task<Item> RequireItemAsync(string reference)
        => await ItemAsync(reference)
           ?? throw new Xunit.Sdk.XunitException(
               $"no item with ref '{reference}' was imported into run {RunItemId}");

    public async Task<IReadOnlyList<DocBlock>> BlocksAsync(string docSlug)
    {
        await using var db = NewContext();
        var docId = await db.Docs
            .Where(d => d.WorkspaceId == WorkspaceId && d.Slug == docSlug)
            .Select(d => d.Id)
            .FirstOrDefaultAsync(Ct);

        return docId == Guid.Empty
            ? []
            : await db.DocBlocks.Where(b => b.DocId == docId).OrderBy(b => b.Seq).ToListAsync(Ct);
    }

    public int Unmapped(string category)
        => Outcome.Unmapped.Shapes.Where(s => s.Category == category).Sum(s => s.Count);
}

internal static class Corpus
{
    /// <summary>The fixture as it sits beside the test assembly, copied there by the project file.</summary>
    public static string Fixture(string runName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", runName);

    public static async Task<ImportedRun> ImportAsync(
        PostgresFixture fixture,
        string runName,
        string? workspaceSlug = null)
    {
        var slug = workspaceSlug ?? $"imp-{Guid.CreateVersion7():n}";
        var outcome = await new Importer(fixture.ConnectionString, slug).ImportAsync([Fixture(runName)], Ct);

        await using var db = fixture.NewContext();
        var workspace = await db.Workspaces.FirstAsync(w => w.Slug == slug, Ct);
        var runItem = await db.Items.FirstAsync(
            i => i.WorkspaceId == workspace.Id && i.Kind == "run" && i.Ref == runName,
            Ct);

        return new ImportedRun(fixture, slug, workspace.Id, runItem.Id, outcome);
    }

    /// <summary>Every row count the importer is responsible for, for the idempotency comparison.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> CensusAsync(ImportedRun run)
    {
        await using var db = run.NewContext();
        var workspaceId = run.WorkspaceId;
        var docIds = await db.Docs.Where(d => d.WorkspaceId == workspaceId).Select(d => d.Id).ToListAsync(Ct);
        var itemIds = await db.Items.Where(i => i.WorkspaceId == workspaceId).Select(i => i.Id).ToListAsync(Ct);

        return new Dictionary<string, int>
        {
            ["item"] = itemIds.Count,
            ["item_criterion"] = await db.ItemCriteria.CountAsync(c => itemIds.Contains(c.ItemId), Ct),
            ["doc"] = docIds.Count,
            ["doc_block"] = await db.DocBlocks.CountAsync(b => docIds.Contains(b.DocId), Ct),
            ["assumption"] = await db.Assumptions.CountAsync(a => a.WorkspaceId == workspaceId, Ct),
            ["note"] = await db.Notes.CountAsync(n => n.WorkspaceId == workspaceId, Ct),
            ["constraint_rule"] = await db.ConstraintRules.CountAsync(c => c.WorkspaceId == workspaceId, Ct),
            ["verification"] = await db.Verifications.CountAsync(v => v.WorkspaceId == workspaceId, Ct),
            ["artifact"] = await db.Artifacts.CountAsync(a => itemIds.Contains(a.ItemId), Ct),
            ["attempt"] = await db.Attempts.CountAsync(a => itemIds.Contains(a.ItemId), Ct),
            ["event"] = await db.Events.CountAsync(e => e.WorkspaceId == workspaceId, Ct),
        };
    }
}
