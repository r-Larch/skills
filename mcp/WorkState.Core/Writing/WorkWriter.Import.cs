using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>Where an imported record came from: a repo-relative file and, optionally, a heading or line ref in it.</summary>
public sealed record ImportSourceArg(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("anchor")] string? Anchor = null);

/// <summary>One row of <c>work_import</c>'s batch. The JSON names are the ones the refusals print.</summary>
public sealed record ImportItemArg(
    [property: JsonPropertyName("source")] ImportSourceArg Source,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("why")] string Why,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("ref")] string? Ref = null,
    [property: JsonPropertyName("parent")] string? Parent = null,
    [property: JsonPropertyName("intent")] string? Intent = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("criteria")] string[]? Criteria = null,
    [property: JsonPropertyName("verify_cmd")] string? VerifyCmd = null,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("route")] string? Route = null);

public sealed partial class WorkWriter
{
    private static readonly string[] ImportItemKinds = ["run", "phase", "task", "followup"];
    private static readonly string[] ImportThreadKinds = ["note", "assumption", "rule"];

    private sealed record ImportProject(Guid Id, string Ref);

    private sealed record Placed(Guid Id, string Ref, string Kind, Guid? RefScopeId, string? ScopeRef);

    private sealed record ImportedRow(
        Guid Id,
        string Kind,
        Guid? ItemId,
        Guid? NoteId,
        Guid? AssumptionId,
        Guid? ConstraintId,
        string? ImportedStatus);

    private sealed record ImportLine(string Action, string Kind, string Ref, string Source, string? Title, string? Reason);

    private sealed record ImportStep(ImportLine? Line, string? Refusal)
    {
        public static ImportStep Done(ImportLine line) => new(line, null);

        public static ImportStep No(string refusal) => new(null, refusal);
    }

    /// <summary>
    /// The import tool's writer: one transactional batch of what an agent judged still true of a
    /// repo's current code. Every row goes through the same guards and inserts as work_create,
    /// work_note, work_assume and work_constrain, and carries its source — so a second import of the
    /// same source updates its row rather than adding one. There is no imported <c>done</c> and no
    /// imported verification, so nothing written here can satisfy a done guard.
    /// </summary>
    public async Task<WriteResult> ImportAsync(
        Guid workspaceId,
        Actor actor,
        string? project,
        IReadOnlyList<ImportItemArg> items,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (Blank(project))
        {
            return WriteResult.No(Refusal.Blocked(
                "work_import without a project.",
                "imported work lands in the project of the repo it was read from",
                "work_brief(repo:{remote:\"…\", root:\"…\"}) prints that project's ref",
                "work_import(project:\"<ref>\", items:[…])"));
        }

        if (items.Count == 0)
        {
            return WriteResult.No(Refusal.Blocked(
                "work_import with no items.",
                "it takes a batch — one call per coherent body of work",
                $"work_import(project:\"{project}\", items:[{{source:{{path:\"docs/plan.md\", anchor:\"## Next\"}}, "
                + "kind:\"followup\", title:\"…\", why:\"…\"}])"));
        }

        var unmet = ImportUnmet(items);
        if (unmet.Count > 0)
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_import — {unmet.Count} problem(s) in the batch. Nothing was written.",
                unmet));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var (target, notReady) = await ImportProjectAsync(connection, transaction, workspaceId, project!.Trim(), cancellationToken);
        if (target is null)
        {
            return WriteResult.No(notReady!);
        }

        var ladder = await LadderAsync(connection, transaction, cancellationToken);
        var parents = await AllowedParentsAsync(connection, transaction, cancellationToken);
        var lines = new List<ImportLine>();

        for (var i = 0; i < items.Count; i++)
        {
            var record = items[i];
            var path = ImportPath(record.Source.Path);
            var anchor = ImportAnchor(record.Source.Anchor);
            var kind = record.Kind.Trim().ToLowerInvariant();
            var call = $"work_import(items[{i}]: {kind} from {Provenance(path, anchor)})";

            try
            {
                var existing = await ImportedAsync(connection, transaction, target.Id, path, anchor, cancellationToken);
                var step = existing is null
                    ? await ImportCreateAsync(
                        connection, transaction, workspaceId, actor, target, record, kind, path, anchor, ladder, parents, call,
                        cancellationToken)
                    : await ImportUpdateAsync(
                        connection, transaction, workspaceId, actor, existing, record, kind, path, anchor, ladder, call,
                        cancellationToken);

                if (step.Refusal is not null)
                {
                    return WriteResult.No(step.Refusal);
                }

                lines.Add(step.Line!);
            }
            catch (PostgresException refused)
            {
                return WriteResult.No(FromPostgres(refused, call));
            }
        }

        if (dryRun)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        else
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return WriteResult.Ok(ImportTable(target, lines, dryRun));
    }

    private static List<Unmet> ImportUnmet(IReadOnlyList<ImportItemArg> items)
    {
        var unmet = new List<Unmet>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < items.Count; i++)
        {
            var record = items[i];
            var at = $"items[{i}]";
            var kind = record?.Kind?.Trim().ToLowerInvariant() ?? string.Empty;

            if (record is null)
            {
                unmet.Add(new Unmet($"{at} is empty", Retry(at, "as {source:{path:\"…\"}, kind:\"…\", title:\"…\", why:\"…\"}")));
                continue;
            }

            if (record.Source is null || Blank(record.Source.Path))
            {
                unmet.Add(new Unmet(
                    $"{at} has no source.path — every record names the repo-relative file it came from",
                    Retry(at, "source:{path:\"docs/plan.md\", anchor:\"Open work\"}")));
            }
            else if (ImportPathProblem(record.Source.Path) is { } problem)
            {
                unmet.Add(new Unmet(
                    $"{at} source.path \"{record.Source.Path}\" {problem}",
                    Retry(at, "source:{path:\"<relative to the repo root, forward slashes>\"}")));
            }
            else
            {
                var key = Provenance(ImportPath(record.Source.Path), ImportAnchor(record.Source.Anchor));
                if (seen.TryGetValue(key, out var first))
                {
                    unmet.Add(new Unmet(
                        $"{at} has the same source as items[{first}] ({key}) — the source is the key a re-import "
                        + "updates by, so two records cannot share one",
                        Retry(at, $"source:{{path:\"{ImportPath(record.Source.Path)}\", anchor:\"<a heading or L<line> of its own>\"}}")));
                }
                else
                {
                    seen[key] = i;
                }
            }

            if (!ImportItemKinds.Contains(kind) && !ImportThreadKinds.Contains(kind))
            {
                unmet.Add(new Unmet(
                    $"{at} kind \"{record.Kind}\" is not importable",
                    Retry(at, "kind: run, phase, task, followup, note, assumption or rule")));
                continue;
            }

            if (Blank(record.Why))
            {
                unmet.Add(new Unmet(
                    $"{at} has no why — the one line that says why this is still true of the current code",
                    Retry(at, "why:\"<what in the code bears it out>\"")));
            }

            if (ImportItemKinds.Contains(kind) && Blank(record.Title))
            {
                unmet.Add(new Unmet($"{at} ({kind}) has no title", Retry(at, "title:\"…\"")));
            }

            if (ImportThreadKinds.Contains(kind) && Blank(record.Body))
            {
                unmet.Add(new Unmet(
                    $"{at} ({kind}) has no body — for a {kind} the body is the text",
                    Retry(at, "body:\"…\"")));
            }

            // A row's ref used to be dropped without a word on thread kinds (import-thread-refs).
            if (!Blank(record.Ref) && kind is "note" or "rule")
            {
                unmet.Add(new Unmet(
                    kind == "note"
                        ? $"{at} (note) carries ref \"{record.Ref}\" — notes have no ref; remove it"
                        : $"{at} (rule) carries ref \"{record.Ref}\" — a rule's ref is numbered C<n> by the server; remove it",
                    Retry(at, "without ref")));
            }

            if (kind == "assumption" && !Blank(record.Ref) && AssumptionRefProblem(record.Ref!) is { } badRef)
            {
                unmet.Add(new Unmet(
                    $"{at} (assumption) ref \"{record.Ref}\" {badRef}",
                    [Retry(at, "ref:\"<one word, e.g. export-reachable>\""), Retry(at, "without ref — the server numbers it A<n>")]));
            }

            if (ImportItemKinds.Contains(kind) && !Blank(record.Ref) && record.Ref!.Contains('/'))
            {
                unmet.Add(new Unmet(
                    $"{at} ({kind}) ref \"{record.Ref.Trim()}\" contains '/' — '/' is the qualifier separator "
                    + "(project/run/ref), so a ref holding one could never be called back",
                    [Retry(at, $"ref:\"{SlashFree(record.Ref)}\""), Retry(at, "without ref — the server numbers it")]));
            }

            if (!Blank(record.Status) && ImportStatus(record.Status) is null)
            {
                unmet.Add(new Unmet(
                    $"{at} status \"{record.Status}\" — imported work is open work: there is no imported done, "
                    + "and nothing imported counts as verified",
                    [
                        Retry(at, "status:\"open\" (the default), \"ready\" or \"blocked\""),
                        Retry(at, "left out — finished work is history, not work"),
                    ]));
            }
        }

        return unmet;
    }

    private static string? AssumptionRefProblem(string reference)
    {
        var value = reference.Trim();
        return value.Contains('/') || value.Any(char.IsWhiteSpace)
            ? "is not one word — an assumption ref is numbered within its run, and run/ref is how it is qualified"
            : null;
    }

    private static string Retry(string at, string fix) => $"work_import(project:\"…\", items:[…]) with {at} {fix}";

    private static ItemStatus? ImportStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "open" => ItemStatus.Open,
        "ready" => ItemStatus.Ready,
        "blocked" => ItemStatus.Blocked,
        _ => null,
    };

    private static string ImportPath(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }

    private static string ImportAnchor(string? anchor) => Glyph.Flat(anchor);

    private static string? ImportPathProblem(string path)
    {
        var value = ImportPath(path);
        if (value.StartsWith('/') || (value.Length > 1 && value[1] == ':'))
        {
            return "is absolute — it must be relative to the repo root, so it names the same file in every checkout";
        }

        return value.Split('/').Contains("..")
            ? "leaves the repo — it must be a file inside it"
            : null;
    }

    private static string Provenance(string path, string anchor)
        => anchor.Length == 0 ? $"import:{path}" : $"import:{path}#{anchor}";

    private static async Task<(ImportProject? Project, string? Refusal)> ImportProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        string reference,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT p.id AS "Id", p.ref AS "Ref"
            FROM   item p
            WHERE  p.workspace_id = @workspace AND p.kind = 'project' AND lower(p.ref) = lower(@ref)
            FOR    NO KEY UPDATE;
            """;

        var project = await connection.QuerySingleOrDefaultAsync<ImportProject>(new CommandDefinition(
            sql, new { workspace = workspaceId, @ref = reference }, transaction, cancellationToken: cancellationToken));

        if (project is null)
        {
            return (null, Refusal.Blocked(
                $"work_import(project:\"{reference}\") — no project has that ref.",
                "an import lands in the project bound to the repo it read, and it must exist first",
                "work_brief(repo:{remote:\"…\", root:\"…\"}) finds the repo's project, or says it has none",
                $"work_create(items:[{{kind:\"project\", ref:\"{reference}\", title:\"…\", repo:{{remote:\"…\", root:\"…\"}}}}]) "
                + "creates it, bound — an orchestrator's call; ask the human first"));
        }

        var bound = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM project_repo WHERE project_item_id = @id);",
            new { id = project.Id },
            transaction,
            cancellationToken: cancellationToken));

        return bound
            ? (project, null)
            : (null, Refusal.Blocked(
                $"work_import(project:\"{project.Ref}\") — {project.Ref} is bound to no repo.",
                "the source paths are relative to a repo, so the project has to say which one",
                $"work_update(ref:\"{project.Ref}\", repo:{{remote:\"<git remote get-url origin>\", root:\"<main checkout root>\"}}) "
                + "binds it — confirm with the human that this is the repo's project"));
    }

    private static async Task<Dictionary<string, string[]>> AllowedParentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<(string Key, string Parents)>(new CommandDefinition(
            "SELECT key, array_to_string(allowed_parent_kinds, ',') FROM item_kind;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        return rows.ToDictionary(r => r.Key, r => r.Parents.Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<ImportedRow?> ImportedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid projectId,
        string path,
        string anchor,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT id AS "Id", kind AS "Kind", item_id AS "ItemId", note_id AS "NoteId",
                   assumption_id AS "AssumptionId", constraint_id AS "ConstraintId",
                   imported_status AS "ImportedStatus"
            FROM   import_record
            WHERE  project_item_id = @project AND source_path = @path AND source_anchor = @anchor
            FOR    UPDATE;
            """;

        return await connection.QuerySingleOrDefaultAsync<ImportedRow>(new CommandDefinition(
            sql, new { project = projectId, path, anchor }, transaction, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// A ref inside the import's project, read on the batch's own transaction — so a parent created
    /// by an earlier row of the same batch resolves, which a separate connection could not see.
    /// </summary>
    private static async Task<(Placed? Item, string? Refusal)> InProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportProject project,
        string asked,
        string call,
        CancellationToken cancellationToken)
    {
        var reference = asked.Trim();
        var parts = reference.Split('/');
        var qualifier = parts.Length > 1 ? string.Join('/', parts.SkipLast(1)) : null;
        var leaf = parts[^1];

        const string sql =
            """
            SELECT i.id AS "Id", i.ref AS "Ref", i.kind AS "Kind", i.ref_scope_id AS "RefScopeId", s.ref AS "ScopeRef"
            FROM   item i
            LEFT   JOIN item s ON s.id = i.ref_scope_id
            WHERE  (i.id = @project OR @project = ANY(i.ancestors))
              AND  lower(i.ref) = lower(@leaf)
              AND  (@qualifier IS NULL OR lower(s.ref) = lower(@qualifier))
              AND  i.status NOT IN ('cancelled','superseded')
            ORDER  BY i.depth, s.ref;
            """;

        var matches = (await connection.QueryAsync<Placed>(new CommandDefinition(
            sql,
            new { project = project.Id, leaf, qualifier },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, Refusal.Blocked(
                $"{call} — parent \"{reference}\" is no item of project {project.Ref}, and no earlier row of this batch created it.",
                "a parent is a ref that exists in the project or is created above this row in the same batch",
                "work_import(project:\"…\", items:[…]) with the row that creates the parent above this one",
                $"work_tree(scope:\"{project.Ref}\") to see the refs that exist")),
            _ => (null, Refusal.Blocked(
                $"{call} — parent \"{reference}\" matches {matches.Count} items in project {project.Ref}.",
                "a ref is numbered within its run, and this one exists in more than one",
                [.. matches.Take(4).Select(m => $"work_import(project:\"{project.Ref}\", items:[…]) with parent:\"{m.ScopeRef}/{m.Ref}\"")])),
        };
    }

    private async Task<ImportStep> ImportCreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Actor actor,
        ImportProject project,
        ImportItemArg record,
        string kind,
        string path,
        string anchor,
        Dictionary<string, KindRules> ladder,
        Dictionary<string, string[]> parents,
        string call,
        CancellationToken cancellationToken)
    {
        Placed? parent = null;
        if (!Blank(record.Parent))
        {
            var (found, refusal) = await InProjectAsync(connection, transaction, project, record.Parent!, call, cancellationToken);
            if (found is null)
            {
                return ImportStep.No(refusal!);
            }

            parent = found;
        }

        var source = Provenance(path, anchor);

        if (ImportThreadKinds.Contains(kind))
        {
            return await ImportThreadAsync(
                connection, transaction, workspaceId, actor, project, record, kind, path, anchor, parent, cancellationToken);
        }

        var shortfall = Shortfall(kind, ladder[kind], new NewItemArg(kind, record.Title!, VerifyCmd: record.VerifyCmd, Criteria: record.Criteria));
        if (shortfall is not null)
        {
            return ImportStep.No(Refusal.Blocked(
                $"{call} — refused exactly as work_create refuses it.",
                [
                    new Unmet(
                        $"a {kind} needs a verify_cmd that can fail and at least one done-criterion; the source gave "
                        + $"{(Blank(record.VerifyCmd) ? "no command" : "a command")} and {record.Criteria?.Length ?? 0} criteria",
                        [
                            $"work_import(project:\"{project.Ref}\", items:[…]) with verify_cmd:\"…\", criteria:[\"…\"] "
                            + "when the source or the code names what judges it",
                            $"work_import(project:\"{project.Ref}\", items:[…]) with kind:\"followup\" when it names no "
                            + "such thing — a followup needs neither, and stays open work",
                        ]),
                ]));
        }

        parent ??= kind is "run" or "followup"
            ? new Placed(project.Id, project.Ref, "project", project.Id, project.Ref)
            : null;

        if (parent is null)
        {
            return ImportStep.No(Refusal.Blocked(
                $"{call} without a parent.",
                $"a {kind} hangs off a {string.Join(" or ", parents[kind])}; only a run or a followup defaults to the project",
                $"work_import(project:\"{project.Ref}\", items:[…]) with parent:\"<a {string.Join(" or ", parents[kind])} "
                + "ref, existing or created above in this batch>\""));
        }

        if (!parents[kind].Contains(parent.Kind))
        {
            return ImportStep.No(Refusal.Blocked(
                $"{call} — a {kind} cannot hang off {parent.Ref}, which is a {parent.Kind}.",
                $"a {kind}'s parent is a {string.Join(" or ", parents[kind])}",
                $"work_import(project:\"{project.Ref}\", items:[…]) with parent:\"<a {string.Join(" or ", parents[kind])} ref>\""));
        }

        var refScope = parent.RefScopeId == parent.Id ? parent.Id : parent.RefScopeId;
        var reference = Blank(record.Ref)
            ? await DerivedRefAsync(
                connection, transaction, workspaceId, new ScopeRefLite(parent.Id, parent.Ref, parent.RefScopeId), refScope, kind, cancellationToken)
            : record.Ref!.Trim();

        var row = new NewRow(
            Guid.CreateVersion7(),
            workspaceId,
            parent.Id,
            kind,
            reference,
            await NextSeqAsync(connection, transaction, parent.Id, cancellationToken),
            Glyph.Flat(record.Title),
            Blank(record.Intent) ? null : record.Intent,
            Blank(record.VerifyCmd) ? null : record.VerifyCmd,
            Source: source);

        await InsertItemAsync(connection, transaction, row, cancellationToken);
        await CriteriaAsync(connection, transaction, row.Id, record.Criteria ?? [], cancellationToken);
        await UnarchiveForNewWorkAsync(connection, transaction, workspaceId, row.Id, reference, actor, cancellationToken);

        var status = ImportStatus(record.Status)!.Value;
        if (status != ItemStatus.Open)
        {
            await SetImportStatusAsync(connection, transaction, row.Id, status, cancellationToken);
        }

        await RecordImportAsync(
            connection, transaction, workspaceId, actor, project.Id, path, anchor, kind, record.Why,
            itemId: row.Id, status: status, cancellationToken: cancellationToken);

        await EventAsync(
            connection, transaction, workspaceId, row.Id, actor, "import",
            $"{reference} ({kind}) imported from {source}: {Glyph.Flat(record.Title)}",
            new { action = "created", kind, path, anchor, why = record.Why, parent = parent.Ref },
            cancellationToken);

        return ImportStep.Done(new ImportLine("created", kind, reference, source, record.Title, null));
    }

    private static async Task<ImportStep> ImportThreadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Actor actor,
        ImportProject project,
        ImportItemArg record,
        string kind,
        string path,
        string anchor,
        Placed? parent,
        CancellationToken cancellationToken)
    {
        var source = Provenance(path, anchor);
        var scope = parent ?? new Placed(project.Id, project.Ref, "project", project.Id, project.Ref);
        var body = record.Body!.Trim();
        Guid? noteId = null, assumptionId = null, constraintId = null;
        string shown;

        switch (kind)
        {
            case "note":
                noteId = await InsertNoteAsync(
                    connection, transaction, workspaceId, actor, NoteKind.CarryForward, body, project.Id, parent?.Id,
                    cancellationToken);
                shown = parent is null ? "note" : $"note → {parent.Ref}";
                break;

            case "assumption":
                var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);
                var chosen = Blank(record.Ref) ? null : record.Ref!.Trim();
                if (chosen is not null
                    && await AssumptionRefTakenAsync(connection, transaction, item.WorkspaceId, item.RefScopeId, chosen, cancellationToken))
                {
                    return ImportStep.No(Refusal.Blocked(
                        $"work_import(assumption from {source}) — ref \"{chosen}\" is taken under {scope.Ref}.",
                        "an assumption ref is unique within its run, and this one already names another assumption",
                        $"work_import(project:\"{project.Ref}\", items:[…]) with a ref of its own on that row",
                        $"work_import(project:\"{project.Ref}\", items:[…]) without ref — the server numbers it A<n>"));
                }

                var minted = await AssumeAsync(
                    connection, transaction, item, actor, body, EvidenceLevel.Asserted, ReversalCost.Medium, null, cancellationToken,
                    chosen);
                assumptionId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                    """
                    UPDATE assumption SET evidence_note = @note
                     WHERE scope_item_id = @scope AND ref = @ref
                    RETURNING id;
                    """,
                    new { note = $"{source} — {record.Why.Trim()}", scope = scope.Id, @ref = minted },
                    transaction,
                    cancellationToken: cancellationToken));
                shown = minted;
                break;

            default:
                var ruleKind = Blank(record.Route) ? ConstraintKind.DomainRule : ConstraintKind.RedLine;
                var (id, rule) = await InsertConstraintAsync(
                    connection, transaction, workspaceId, scope.Id, ruleKind,
                    new ConstrainArgs(Text: body, Route: record.Route), null, cancellationToken);
                constraintId = id;
                shown = rule;
                break;
        }

        await RecordImportAsync(
            connection, transaction, workspaceId, actor, project.Id, path, anchor, kind, record.Why,
            noteId: noteId, assumptionId: assumptionId, constraintId: constraintId, cancellationToken: cancellationToken);

        await EventAsync(
            connection, transaction, workspaceId, scope.Id, actor, "import",
            $"{shown} ({kind}) imported from {source}: {Glyph.Flat(body)}",
            new { action = "created", kind, path, anchor, why = record.Why, scope = scope.Ref },
            cancellationToken);

        return ImportStep.Done(new ImportLine("created", kind, shown, source, body, null));
    }

    /// <summary>
    /// A re-import of a known source. It rewrites what the source says — title, intent, criteria,
    /// body — and never what work has happened since: an item whose status is not the one the
    /// import left, or that was started, checked or verified, is reported and left alone.
    /// </summary>
    private async Task<ImportStep> ImportUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Actor actor,
        ImportedRow existing,
        ImportItemArg record,
        string kind,
        string path,
        string anchor,
        Dictionary<string, KindRules> ladder,
        string call,
        CancellationToken cancellationToken)
    {
        var source = Provenance(path, anchor);

        if (existing.Kind != kind)
        {
            return ImportStep.Done(new ImportLine(
                "skipped", kind, "—", source, record.Title ?? record.Body,
                $"imported as a {existing.Kind} before; a re-import never changes a record's kind"));
        }

        if (existing.ItemId is not { } itemId)
        {
            return await ImportThreadUpdateAsync(connection, transaction, workspaceId, actor, existing, record, kind, source, cancellationToken);
        }

        var item = await LoadAsync(connection, transaction, itemId, cancellationToken);
        var moved = await MovedSinceImportAsync(connection, transaction, item, existing.ImportedStatus, cancellationToken);
        if (moved is not null)
        {
            return ImportStep.Done(new ImportLine("skipped", kind, item.Ref, source, item.Title, $"moved since import ({moved})"));
        }

        var shortfall = Shortfall(kind, ladder[kind], new NewItemArg(
            kind,
            record.Title!,
            VerifyCmd: Blank(record.VerifyCmd) ? item.VerifyCmd : record.VerifyCmd,
            Criteria: record.Criteria ?? ["kept"]));
        if (shortfall is not null)
        {
            return ImportStep.No(Refusal.Blocked(
                $"{call} — the update would leave {item.Ref} unable to close.",
                "a task needs a verify_cmd and at least one done-criterion",
                $"work_import(project:\"…\", items:[…]) with verify_cmd:\"…\" and criteria:[\"…\"] on {item.Ref}'s row"));
        }

        await using (var command = new NpgsqlCommand(
            """
            UPDATE item
               SET title      = @title,
                   intent     = coalesce(@intent, intent),
                   verify_cmd = coalesce(@verify, verify_cmd),
                   source     = @source,
                   revision   = revision + 1
             WHERE id = @id;
            """,
            connection,
            transaction))
        {
            Bind(command, "id", item.Id);
            Bind(command, "title", Glyph.Flat(record.Title));
            Bind(command, "intent", Blank(record.Intent) ? null : record.Intent);
            Bind(command, "verify", Blank(record.VerifyCmd) ? null : record.VerifyCmd);
            Bind(command, "source", source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (record.Criteria is { } criteria)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM item_criterion WHERE item_id = @id;",
                new { id = item.Id },
                transaction,
                cancellationToken: cancellationToken));
            await CriteriaAsync(connection, transaction, item.Id, criteria, cancellationToken);
        }

        var status = Blank(record.Status) ? item.Status : ImportStatus(record.Status)!.Value;
        if (status != item.Status)
        {
            await SetImportStatusAsync(connection, transaction, item.Id, status, cancellationToken);
        }

        await TouchImportAsync(connection, transaction, actor, existing.Id, record.Why, status, cancellationToken);

        await EventAsync(
            connection, transaction, workspaceId, item.Id, actor, "import",
            $"{item.Ref} ({kind}) re-imported from {source}: {Glyph.Flat(record.Title)}",
            new { action = "updated", kind, path, anchor, why = record.Why },
            cancellationToken);

        return ImportStep.Done(new ImportLine("updated", kind, item.Ref, source, record.Title, null));
    }

    private static async Task<ImportStep> ImportThreadUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Actor actor,
        ImportedRow existing,
        ImportItemArg record,
        string kind,
        string source,
        CancellationToken cancellationToken)
    {
        var body = record.Body!.Trim();

        var (sql, moved) = kind switch
        {
            "note" => (
                """
                UPDATE note SET body = @body WHERE id = @id AND consumed_at IS NULL
                RETURNING 'note';
                """,
                "delivered since import — a note is read once and not rewritten after"),
            "assumption" => (
                """
                UPDATE assumption SET statement = @body, evidence_note = @evidence
                 WHERE id = @id AND state = 'open'
                RETURNING ref;
                """,
                "resolved since import"),
            _ => (
                """
                UPDATE constraint_rule SET text = @body, route = coalesce(@route, route)
                 WHERE id = @id AND active
                RETURNING ref;
                """,
                "retired since import"),
        };

        var shown = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            sql,
            new
            {
                id = existing.NoteId ?? existing.AssumptionId ?? existing.ConstraintId,
                body,
                evidence = $"{source} — {record.Why.Trim()}",
                route = Blank(record.Route) ? null : record.Route!.Trim(),
            },
            transaction,
            cancellationToken: cancellationToken));

        if (shown is null)
        {
            return ImportStep.Done(new ImportLine("skipped", kind, "—", source, body, moved));
        }

        if (kind == "assumption" && !Blank(record.Ref) && !string.Equals(record.Ref!.Trim(), shown, StringComparison.OrdinalIgnoreCase))
        {
            return ImportStep.No(Refusal.Blocked(
                $"work_import(assumption from {source}) — ref \"{record.Ref!.Trim()}\", but this source was imported as {shown}.",
                "a re-import updates the text of what a source named and never renames it",
                $"work_import(project:\"…\", items:[…]) with ref:\"{shown}\" or without ref on that row"));
        }

        await TouchImportAsync(connection, transaction, actor, existing.Id, record.Why, null, cancellationToken);

        await EventAsync(
            connection, transaction, workspaceId, null, actor, "import",
            $"{shown} ({kind}) re-imported from {source}: {Glyph.Flat(body)}",
            new { action = "updated", kind, source, why = record.Why },
            cancellationToken);

        return ImportStep.Done(new ImportLine("updated", kind, shown, source, body, null));
    }

    /// <summary>Null when the item is as the import left it; otherwise what moved it, for the skipped line.</summary>
    private static async Task<string?> MovedSinceImportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        string? importedStatus,
        CancellationToken cancellationToken)
    {
        if (Pg.Label(item.Status) != importedStatus)
        {
            return $"now {Transitions.Name(item.Status)}";
        }

        if (item.StartedAt is not null)
        {
            return "it was started";
        }

        const string sql =
            """
            SELECT CASE
                     WHEN EXISTS (SELECT 1 FROM verification WHERE item_id = @id) THEN 'evidence was recorded against it'
                     WHEN EXISTS (SELECT 1 FROM item_criterion WHERE item_id = @id AND checked) THEN 'a criterion was checked'
                   END;
            """;

        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            sql, new { id = item.Id }, transaction, cancellationToken: cancellationToken));
    }

    private static async Task SetImportStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        ItemStatus status,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE item SET status = @status::item_status, revision = revision + 1 WHERE id = @id;",
            new { id = itemId, status = Pg.Label(status) },
            transaction,
            cancellationToken: cancellationToken));

    private static async Task RecordImportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Actor actor,
        Guid projectId,
        string path,
        string anchor,
        string kind,
        string why,
        Guid? itemId = null,
        Guid? noteId = null,
        Guid? assumptionId = null,
        Guid? constraintId = null,
        ItemStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            INSERT INTO import_record
                (id, workspace_id, project_item_id, source_path, source_anchor, kind, item_id, note_id,
                 assumption_id, constraint_id, why, imported_status, session_id, imported_by)
            VALUES (@id, @workspace, @project, @path, @anchor, @kind, @item, @note,
                    @assumption, @constraint, @why, @status, @session, @actor);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = Guid.CreateVersion7(),
                workspace = workspaceId,
                project = projectId,
                path,
                anchor,
                kind,
                item = itemId,
                note = noteId,
                assumption = assumptionId,
                constraint = constraintId,
                why = why.Trim(),
                status = status is { } s ? Pg.Label(s) : null,
                session = actor.SessionId,
                actor = actor.Session ?? actor.Name,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task TouchImportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Actor actor,
        Guid recordId,
        string why,
        ItemStatus? status,
        CancellationToken cancellationToken)
        => await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE import_record
               SET why             = @why,
                   imported_status = coalesce(@status, imported_status),
                   session_id      = @session,
                   imported_by     = @actor,
                   updated_at      = now()
             WHERE id = @id;
            """,
            new
            {
                id = recordId,
                why = why.Trim(),
                status = status is { } s ? Pg.Label(s) : null,
                session = actor.SessionId,
                actor = actor.Session ?? actor.Name,
            },
            transaction,
            cancellationToken: cancellationToken));

    private static string ImportTable(ImportProject project, IReadOnlyList<ImportLine> lines, bool dryRun)
    {
        int Count(string action) => lines.Count(l => l.Action == action);
        var tally = $"{Count("created")} created, {Count("updated")} updated, {Count("skipped")} skipped";

        var text = new List<string>
        {
            dryRun
                ? $"DRY RUN  project {project.Ref} — nothing was written; this batch would leave {tally}"
                : $"OK  project {project.Ref} — {tally}",
        };

        var refWidth = Math.Max(6, lines.Max(l => l.Ref.Length));

        foreach (var line in lines)
        {
            text.Add($"    {Glyph.Pad(line.Action, 8)} {Glyph.Pad(line.Kind, 10)} {Glyph.Pad(line.Ref, refWidth)}  {line.Source}");
            if (line.Title is { Length: > 0 })
            {
                text.AddRange(BriefRenderer.Wrap(new string(' ', 6), line.Title, 6));
            }

            if (line.Reason is { Length: > 0 })
            {
                text.AddRange(BriefRenderer.Wrap("      ↳ ", line.Reason, 8));
            }
        }

        text.Add(string.Empty);
        text.Add(dryRun
            ? $"Next: show the human this table; on their yes, work_import(project:\"{project.Ref}\", items:[…the same…]) "
              + "without dry_run writes it."
            : $"Next: work_tree(scope:\"{project.Ref}\") to check the result against the plan; "
              + "work_get(refs:[…]) prints each item's source and why. Re-running the same sources updates them.");

        return string.Join("\n", text);
    }
}
