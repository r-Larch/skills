using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Import.Model;

namespace WorkState.Import.Writing;

public sealed record RunWriteResult(
    string Project,
    string Run,
    int Files,
    int FilesSkipped,
    int Docs,
    int Blocks,
    int Items,
    int Assumptions,
    int Notes,
    int Constraints,
    int Verifications,
    int Artifacts,
    int Events,
    int Attempts,
    int ReplacementChars,
    int ControlChars);

/// <summary>
/// The write side. Re-importing the same run must update in place, so every row is addressed by a key
/// the corpus itself supplies — <c>(project, run, ref)</c> for items and assumptions, the file path for
/// docs, and the row's own content for the evidence tables. Nothing here issues an unscoped delete: the
/// only deletion is of doc blocks that a shrinking file no longer has, and it is scoped to that doc.
/// </summary>
public sealed class StoreWriter(string connectionString, string workspaceSlug = "import")
{
    public async Task<RunWriteResult> WriteAsync(RunImport run, CancellationToken cancellationToken)
    {
        await using var db = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString));

        var workspace = await EnsureWorkspaceAsync(db, cancellationToken);
        var project = await EnsureProjectAsync(db, workspace, run, cancellationToken);
        var runItem = await EnsureRunAsync(db, workspace, project, run, cancellationToken);

        var byRef = await WriteItemsAsync(db, workspace, runItem, run, cancellationToken);
        byRef[""] = runItem.Id;

        var (docs, blocks) = await WriteDocsAsync(db, workspace, runItem, run, cancellationToken);
        var assumptions = await WriteAssumptionsAsync(db, workspace, runItem, run, byRef, cancellationToken);
        var notes = await WriteNotesAsync(db, workspace, runItem, run, byRef, cancellationToken);
        var constraints = await WriteConstraintsAsync(db, workspace, runItem, run, cancellationToken);
        var verifications = await WriteVerificationsAsync(db, workspace, runItem, run, byRef, cancellationToken);
        var artifacts = await WriteArtifactsAsync(db, run, byRef, cancellationToken);
        var attempts = await WriteAttemptsAsync(db, workspace, runItem, run, byRef, cancellationToken);
        var events = await WriteEventsAsync(db, workspace, runItem, run, byRef, cancellationToken);

        return new RunWriteResult(
            run.ProjectSlug,
            run.RunSlug,
            run.FilesSeen,
            run.FilesSkipped,
            docs,
            blocks,
            run.Items.Count,
            assumptions,
            notes,
            constraints,
            verifications,
            artifacts,
            events,
            attempts,
            run.ReplacementChars,
            run.ControlChars);
    }

    private async Task<Workspace> EnsureWorkspaceAsync(WorkStateDbContext db, CancellationToken token)
    {
        var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.Slug == workspaceSlug, token);
        if (existing is not null)
        {
            return existing;
        }

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Slug = workspaceSlug };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(token);
        return workspace;
    }

    private static async Task<Item> EnsureProjectAsync(
        WorkStateDbContext db,
        Workspace workspace,
        RunImport run,
        CancellationToken token)
    {
        var existing = await db.Items.FirstOrDefaultAsync(
            i => i.WorkspaceId == workspace.Id && i.Kind == "project" && i.Ref == run.ProjectSlug,
            token);

        if (existing is not null)
        {
            return existing;
        }

        var last = await db.Items
            .Where(i => i.WorkspaceId == workspace.Id && i.ParentId == null)
            .MaxAsync(i => (decimal?)i.Seq, token);

        var project = new Item
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            Kind = "project",
            Ref = run.ProjectSlug,
            Seq = SeqAllocator.Between(last, null),
            Title = run.ProjectSlug,
        };

        db.Items.Add(project);
        await db.SaveChangesAsync(token);

        db.ProjectConfigs.Add(new ProjectConfig { ItemId = project.Id, RepoRoot = run.RepoRoot });
        await db.SaveChangesAsync(token);
        return project;
    }

    private static async Task<Item> EnsureRunAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item project,
        RunImport run,
        CancellationToken token)
    {
        var existing = await db.Items.FirstOrDefaultAsync(
            i => i.ParentId == project.Id && i.Ref == run.RunSlug,
            token);

        if (existing is not null)
        {
            if (run.Goal.Length > 0 && existing.Intent is null)
            {
                existing.Intent = run.Goal;
                await db.SaveChangesAsync(token);
            }

            return existing;
        }

        var last = await db.Items
            .Where(i => i.ParentId == project.Id)
            .MaxAsync(i => (decimal?)i.Seq, token);

        var runItem = new Item
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            ParentId = project.Id,
            Kind = "run",
            Ref = run.RunSlug,
            Seq = SeqAllocator.Between(last, null),
            Title = run.RunSlug,
            Intent = run.Goal.Length > 0 ? run.Goal : null,
        };

        db.Items.Add(runItem);
        await db.SaveChangesAsync(token);

        db.RunConfigs.Add(new RunConfig
        {
            ItemId = runItem.Id,
            ProjectItemId = project.Id,
            WorktreePath = run.RunPath,
            SerializationKey = run.RepoRoot,
            OwnerAgent = "import",
        });
        await db.SaveChangesAsync(token);
        return runItem;
    }

    private static async Task<Dictionary<string, Guid>> WriteItemsAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        CancellationToken token)
    {
        var existing = await db.Items
            .Where(i => i.RefScopeId == runItem.Id && i.Id != runItem.Id)
            .ToDictionaryAsync(i => i.Ref, i => i, StringComparer.OrdinalIgnoreCase, token);

        var byRef = existing.ToDictionary(e => e.Key, e => e.Value.Id, StringComparer.OrdinalIgnoreCase);
        var seqByParent = new Dictionary<Guid, decimal?>();

        foreach (var level in run.Items.Values.GroupBy(Depth).OrderBy(g => g.Key))
        {
            foreach (var source in level.OrderBy(i => i.Order))
            {
                var parentId = source.ParentRef is { } parent && byRef.TryGetValue(parent, out var id)
                    ? id
                    : runItem.Id;

                if (existing.TryGetValue(source.Ref, out var row))
                {
                    Apply(row, source);
                    continue;
                }

                if (!seqByParent.TryGetValue(parentId, out var last))
                {
                    last = await db.Items
                        .Where(i => i.ParentId == parentId)
                        .MaxAsync(i => (decimal?)i.Seq, token);
                }

                var created = new Item
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = workspace.Id,
                    ParentId = parentId,
                    Kind = source.Kind,
                    Ref = source.Ref,
                    Seq = SeqAllocator.Between(last, null),
                    Title = source.Title.Length > 0 ? source.Title : source.Ref,
                };

                Apply(created, source);
                seqByParent[parentId] = created.Seq;
                db.Items.Add(created);
                byRef[source.Ref] = created.Id;
                existing[source.Ref] = created;
            }

            await db.SaveChangesAsync(token);
        }

        await WriteCriteriaAsync(db, run, byRef, token);
        return byRef;

        int Depth(ItemImport item)
        {
            var depth = 0;
            var cursor = item;
            while (cursor.ParentRef is { } parent && run.Items.TryGetValue(parent, out var next) && depth < 8)
            {
                cursor = next;
                depth++;
            }

            return depth;
        }
    }

    private static void Apply(Item row, ItemImport source)
    {
        if (source.Title.Length > 0)
        {
            row.Title = source.Title;
        }

        row.Intent = source.Intent ?? row.Intent;
        row.VerifyCmd = source.VerifyCmd ?? row.VerifyCmd;
        row.GateCriterion = source.GateCriterion ?? row.GateCriterion;
        row.Severity = source.Severity ?? row.Severity;
        row.Source = source.Source ?? row.Source;
        row.RiskTags = source.RiskTags.Count > 0 ? [.. source.RiskTags] : row.RiskTags;

        row.Status = source.Status;
        row.Resolution = source.Resolution;

        // item_closed_at_set: the schema refuses a closed status with no closing time, and refuses a
        // closing time on anything still open.
        var closed = source.Status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;
        row.ClosedAt = closed ? row.ClosedAt ?? DateTimeOffset.UtcNow : null;
        row.ClosedBy = closed ? row.ClosedBy ?? "import" : null;
    }

    private static async Task WriteCriteriaAsync(
        WorkStateDbContext db,
        RunImport run,
        Dictionary<string, Guid> byRef,
        CancellationToken token)
    {
        var itemIds = run.Items.Values
            .Where(i => i.Criteria.Count > 0 && byRef.ContainsKey(i.Ref))
            .Select(i => byRef[i.Ref])
            .ToHashSet();

        if (itemIds.Count == 0)
        {
            return;
        }

        var existing = (await db.ItemCriteria.Where(c => itemIds.Contains(c.ItemId)).ToListAsync(token))
            .Select(c => (c.ItemId, c.Text))
            .ToHashSet();

        var counters = await db.ItemCriteria
            .Where(c => itemIds.Contains(c.ItemId))
            .GroupBy(c => c.ItemId)
            .Select(g => new { ItemId = g.Key, Max = g.Max(c => c.Seq) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Max, token);

        foreach (var item in run.Items.Values.Where(i => i.Criteria.Count > 0))
        {
            if (!byRef.TryGetValue(item.Ref, out var itemId))
            {
                continue;
            }

            foreach (var text in item.Criteria)
            {
                if (!existing.Add((itemId, text)))
                {
                    continue;
                }

                var seq = counters.TryGetValue(itemId, out var max) ? max + 1 : 1;
                counters[itemId] = seq;
                db.ItemCriteria.Add(new ItemCriterion
                {
                    Id = Guid.CreateVersion7(),
                    ItemId = itemId,
                    Seq = seq,
                    Text = text,
                });
            }
        }

        await db.SaveChangesAsync(token);
    }

    private static async Task<(int Docs, int Blocks)> WriteDocsAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        CancellationToken token)
    {
        var existingDocs = await db.Docs
            .Where(d => d.WorkspaceId == workspace.Id && d.ItemId == runItem.Id)
            .ToDictionaryAsync(d => d.Slug, d => d, StringComparer.Ordinal, token);

        var blockCount = 0;

        foreach (var source in run.Docs)
        {
            if (!existingDocs.TryGetValue(source.Slug, out var doc))
            {
                doc = new Doc
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = workspace.Id,
                    ItemId = runItem.Id,
                    Slug = source.Slug,
                    Title = source.Title,
                };
                db.Docs.Add(doc);
                await db.SaveChangesAsync(token);
                existingDocs[source.Slug] = doc;
            }
            else
            {
                doc.Title = source.Title;
            }

            var docId = doc.Id;
            var existingBlocks = await db.DocBlocks
                .Where(b => b.DocId == docId)
                .ToDictionaryAsync(b => b.ShortId, b => b, StringComparer.Ordinal, token);

            foreach (var block in source.Blocks)
            {
                blockCount++;
                if (existingBlocks.TryGetValue(block.ShortId, out var row))
                {
                    row.Heading = block.Heading;
                    row.HeadingLevel = block.HeadingLevel;
                    row.Body = block.Body;
                    row.Seq = block.Seq;
                    existingBlocks.Remove(block.ShortId);
                    continue;
                }

                db.DocBlocks.Add(new DocBlock
                {
                    Id = Guid.CreateVersion7(),
                    DocId = docId,
                    Seq = block.Seq,
                    ShortId = block.ShortId,
                    Heading = block.Heading,
                    HeadingLevel = block.HeadingLevel,
                    Body = block.Body,
                });
            }

            // Blocks the file no longer has. Scoped to this doc — the importer never issues a delete
            // that is not bounded by a single id.
            foreach (var orphan in existingBlocks.Values)
            {
                db.DocBlocks.Remove(orphan);
            }

            await db.SaveChangesAsync(token);
        }

        return (run.Docs.Count, blockCount);
    }

    private static async Task<int> WriteAssumptionsAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        Dictionary<string, Guid> byRef,
        CancellationToken token)
    {
        var existing = await db.Assumptions
            .Where(a => a.RefScopeId == runItem.Id)
            .ToDictionaryAsync(a => a.Ref, a => a, StringComparer.OrdinalIgnoreCase, token);

        var written = 0;

        foreach (var source in run.Assumptions)
        {
            if (!existing.TryGetValue(source.Ref, out var row))
            {
                row = new Assumption
                {
                    Id = Guid.CreateVersion7(),
                    WorkspaceId = workspace.Id,
                    ScopeItemId = runItem.Id,
                    Ref = source.Ref,
                };
                db.Assumptions.Add(row);
                existing[source.Ref] = row;
            }

            row.Statement = source.Statement;
            row.Evidence = source.Evidence;
            row.EvidenceNote = source.EvidenceNote;
            row.Reversal = source.Reversal;
            row.State = source.State;
            row.ResolvedAt = source.State == AssumptionState.Open ? null : source.ResolvedAt ?? DateTimeOffset.UtcNow;
            row.ResolutionNote = source.State == AssumptionState.Open ? null : source.ResolutionNote;
            row.RevisitItemId = source.RevisitRef is { } revisit && byRef.TryGetValue(revisit, out var id)
                ? id
                : null;
            written++;
        }

        await db.SaveChangesAsync(token);
        return written;
    }

    private static async Task<int> WriteNotesAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        Dictionary<string, Guid> byRef,
        CancellationToken token)
    {
        var scope = byRef.Values.Append(runItem.Id).ToHashSet();
        var existing = (await db.Notes
                .Where(n => n.FromItemId != null && scope.Contains(n.FromItemId.Value))
                .Select(n => new { n.FromItemId, n.Kind, n.Body })
                .ToListAsync(token))
            .Select(n => (n.FromItemId, n.Kind, n.Body))
            .ToHashSet();

        var written = 0;

        foreach (var source in run.Notes)
        {
            var from = source.FromRef is { } reference && byRef.TryGetValue(reference, out var id) ? id : runItem.Id;
            if (!existing.Add(((Guid?)from, source.Kind, source.Body)))
            {
                continue;
            }

            db.Notes.Add(new Note
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.Id,
                FromItemId = from,
                Kind = source.Kind,
                Body = source.Body,
                CreatedBy = "import",
            });
            written++;
        }

        await db.SaveChangesAsync(token);
        return run.Notes.Count;
    }

    private static async Task<int> WriteConstraintsAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        CancellationToken token)
    {
        var rows = await db.ConstraintRules
            .Where(c => c.ScopeItemId == runItem.Id)
            .ToListAsync(token);

        var existing = rows.Select(c => (c.Kind, c.Text)).ToHashSet();
        var incoming = run.Constraints.Select(c => (c.Kind, c.Text)).ToHashSet();

        // A ref is numbered across the workspace, not across this run — one BINDING block renders
        // both — so the taken set is every rule in the workspace, including the runs this import is
        // not touching. A re-import matches an existing row by (kind, text) and leaves its ref
        // alone, which is what keeps a rule citable between two briefs.
        var taken = (await db.ConstraintRules
                .Where(c => c.WorkspaceId == workspace.Id)
                .Select(c => c.Ref)
                .ToListAsync(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var next = 1;

        // A rule this CONVENTIONS file no longer states is retracted, not deleted: the fix that
        // reunites a red line with its verb produces different text, and a store that kept both
        // would go on handing an agent the fragment (FINDINGS-live F-L2). Deactivating leaves the
        // row for an audit to find and costs the brief nothing, because it reads only active rules.
        foreach (var row in rows)
        {
            row.Active = incoming.Contains((row.Kind, row.Text));
        }

        foreach (var source in run.Constraints)
        {
            if (!existing.Add((source.Kind, source.Text)))
            {
                continue;
            }

            while (!taken.Add($"C{next}"))
            {
                next++;
            }

            db.ConstraintRules.Add(new ConstraintRule
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.Id,
                ScopeItemId = runItem.Id,
                Ref = $"C{next}",
                Kind = source.Kind,
                Text = source.Text,
                Route = source.Route,
                CoversRisk = [.. source.CoversRisk],
            });
        }

        await db.SaveChangesAsync(token);
        return run.Constraints.Count;
    }

    private static async Task<int> WriteVerificationsAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        Dictionary<string, Guid> byRef,
        CancellationToken token)
    {
        var scope = byRef.Values.Append(runItem.Id).ToHashSet();
        var existing = (await db.Verifications
                .Where(v => scope.Contains(v.ItemId))
                .Select(v => new { v.ItemId, v.Command, v.ResultLine })
                .ToListAsync(token))
            .Select(v => (v.ItemId, v.Command, v.ResultLine))
            .ToHashSet();

        foreach (var source in run.Verifications)
        {
            var itemId = byRef.TryGetValue(source.ItemRef, out var id) ? id : runItem.Id;
            if (!existing.Add((itemId, source.Command, source.ResultLine)))
            {
                continue;
            }

            db.Verifications.Add(new Verification
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = workspace.Id,
                ItemId = itemId,
                Command = source.Command,
                ResultLine = source.ResultLine,
                Passed = source.Passed,
                RunBy = source.RunBy,
                RedCheck = source.RedCheckJson,
            });
        }

        await db.SaveChangesAsync(token);
        return run.Verifications.Count;
    }

    private static async Task<int> WriteArtifactsAsync(
        WorkStateDbContext db,
        RunImport run,
        Dictionary<string, Guid> byRef,
        CancellationToken token)
    {
        var scope = byRef.Values.ToHashSet();
        var existing = (await db.Artifacts
                .Where(a => scope.Contains(a.ItemId))
                .Select(a => new { a.ItemId, a.Kind, a.Value })
                .ToListAsync(token))
            .Select(a => (a.ItemId, a.Kind, a.Value))
            .ToHashSet();

        foreach (var source in run.Artifacts)
        {
            var itemId = byRef.TryGetValue(source.ItemRef, out var id) ? id : byRef[""];
            if (!existing.Add((itemId, source.Kind, source.Value)))
            {
                continue;
            }

            db.Artifacts.Add(new Artifact
            {
                Id = Guid.CreateVersion7(),
                ItemId = itemId,
                Kind = source.Kind,
                Value = source.Value,
                Meta = source.MetaJson,
            });
        }

        await db.SaveChangesAsync(token);
        return run.Artifacts.Count;
    }

    private static async Task<int> WriteAttemptsAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        Dictionary<string, Guid> byRef,
        CancellationToken token)
    {
        var docs = await db.Docs
            .Where(d => d.WorkspaceId == workspace.Id && d.ItemId == runItem.Id)
            .ToDictionaryAsync(d => d.Slug, d => d.Id, StringComparer.Ordinal, token);

        var scope = byRef.Values.ToHashSet();
        var existing = await db.Attempts
            .Where(a => scope.Contains(a.ItemId))
            .ToListAsync(token);

        var written = 0;

        foreach (var source in run.Attempts)
        {
            if (!byRef.TryGetValue(source.ItemRef, out var itemId) || !docs.TryGetValue(source.DocSlug, out var docId))
            {
                continue;
            }

            if (existing.Any(a => a.ItemId == itemId && a.BriefDocId == docId))
            {
                continue;
            }

            var n = existing.Where(a => a.ItemId == itemId).Select(a => a.N).DefaultIfEmpty(0).Max() + 1;
            var attempt = new Attempt
            {
                Id = Guid.CreateVersion7(),
                ItemId = itemId,
                N = n,
                AgentName = source.AgentName,
                BriefDocId = docId,
            };

            db.Attempts.Add(attempt);
            existing.Add(attempt);
            written++;
        }

        await db.SaveChangesAsync(token);
        return run.Attempts.Count;
    }

    private static async Task<int> WriteEventsAsync(
        WorkStateDbContext db,
        Workspace workspace,
        Item runItem,
        RunImport run,
        Dictionary<string, Guid> byRef,
        CancellationToken token)
    {
        var scope = byRef.Values.Append(runItem.Id).ToHashSet();

        // The journal is append-only (doc 02 §11), so a second import must recognise its own earlier
        // entries rather than delete and rewrite them.
        var existing = (await db.Events
                .Where(e => e.ItemId != null && scope.Contains(e.ItemId.Value))
                .Select(e => new { e.ItemId, e.Kind, e.Summary })
                .ToListAsync(token))
            .Select(e => (e.ItemId, e.Kind, e.Summary))
            .ToHashSet();

        var all = run.Events.Append(new EventImport(
            null,
            "imported",
            ActorRole.System,
            "import",
            $"imported {run.FilesSeen} files, {run.Docs.Sum(d => d.Blocks.Count)} blocks, {run.Items.Count} items",
            JsonSerializer.Serialize(new { run.RunPath, run.FilesSeen, run.FilesSkipped })));

        foreach (var source in all)
        {
            var itemId = source.ItemRef is { } reference && byRef.TryGetValue(reference, out var id)
                ? id
                : runItem.Id;

            if (!existing.Add(((Guid?)itemId, source.Kind, source.Summary)))
            {
                continue;
            }

            db.Events.Add(new Event
            {
                WorkspaceId = workspace.Id,
                ItemId = itemId,
                Actor = source.Actor,
                Role = source.Role,
                Kind = source.Kind,
                Summary = source.Summary,
                Payload = source.PayloadJson,
            });
        }

        await db.SaveChangesAsync(token);
        return run.Events.Count + 1;
    }
}
