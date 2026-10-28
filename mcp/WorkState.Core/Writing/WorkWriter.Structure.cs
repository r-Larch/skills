using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>
/// One row of <c>work_create</c>'s batch — decomposition writes 6–20 of these at once. The JSON
/// names are the ones the refusals and the brief print, because a route an agent copies has to be
/// the route the schema accepts (doc 04 §3).
/// </summary>
public sealed record NewItemArg(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("ref")] string? Ref = null,
    [property: JsonPropertyName("parent")] string? Parent = null,
    [property: JsonPropertyName("intent")] string? Intent = null,
    [property: JsonPropertyName("verify_cmd")] string? VerifyCmd = null,
    [property: JsonPropertyName("criteria")] string[]? Criteria = null,
    [property: JsonPropertyName("risk_tags")] string[]? RiskTags = null,
    [property: JsonPropertyName("priority")] int? Priority = null,
    [property: JsonPropertyName("repo")] RepoArg? Repo = null);

/// <summary>One criterion as <c>work_update</c> addresses it: <c>c2</c>, checked, with its evidence.</summary>
public sealed record CriterionArg(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("checked")] bool? Checked = null,
    [property: JsonPropertyName("evidence")] string? Evidence = null);

public sealed record UpdateArgs(
    string? Title = null,
    string? Intent = null,
    string? VerifyCmd = null,
    string[]? RiskTags = null,
    int? Priority = null,
    CriterionArg[]? Criteria = null,
    int? ExpectedRevision = null,
    RepoArg? Repo = null,
    bool? Archived = null);

public sealed partial class WorkWriter
{
    /// <summary>
    /// The cold start. <c>work_brief</c> on an empty server tells the agent to call
    /// <c>work_create</c>, so that call has to work before anything exists — otherwise the
    /// onboarding names a route that refuses (doc 04 §4).
    /// </summary>
    public async Task<Guid> EnsureWorkspaceAsync(string? slug, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            WITH existing AS (
              SELECT id FROM workspace WHERE slug = @slug
            ), created AS (
              INSERT INTO workspace (id, slug)
              SELECT @id, @slug WHERE NOT EXISTS (SELECT 1 FROM existing)
              RETURNING id
            )
            SELECT id FROM existing UNION ALL SELECT id FROM created;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new { id = Guid.CreateVersion7(), slug = string.IsNullOrWhiteSpace(slug) ? "default" : slug.Trim() },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// doc 01 §3.2. Batch, because decomposition creates six to twenty items in one call, and the
    /// refusal for a task without a verification command or a done-criterion is the skills'
    /// "a brief that omits done-criteria or a verification command is a defect", made unwritable.
    /// </summary>
    public async Task<WriteResult> CreateAsync(
        Guid workspaceId,
        Actor actor,
        IReadOnlyList<NewItemArg> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return WriteResult.No(Refusal.Blocked(
                "work_create with no items.",
                "it takes an array — a decomposition is one call, not twelve",
                "work_create(items:[{kind:\"task\", parent:\"2\", title:\"…\", verify_cmd:\"…\", "
                + "criteria:[\"…\"]}])"));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var ladder = await LadderAsync(connection, transaction, cancellationToken);
        var written = new List<string>();
        var created = new List<(int Line, Guid Id, string Ref, string Tail)>();

        foreach (var wanted in items)
        {
            var kind = wanted.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!ladder.TryGetValue(kind, out var rules))
            {
                return WriteResult.No(Refusal.Blocked(
                    $"work_create(items:[{{kind:\"{wanted.Kind}\"}}]) — no such kind.",
                    $"the ladder is fixed: {string.Join(", ", ladder.Keys.Order())}",
                    "work_create(items:[{kind:\"task\", parent:\"…\", title:\"…\", verify_cmd:\"…\", "
                    + "criteria:[\"…\"]}])"));
            }

            if (Blank(wanted.Title))
            {
                return WriteResult.No(Refusal.Blocked(
                    $"work_create(items:[{{kind:\"{kind}\"}}]) without a title.",
                    "the title is the handle a board row is read by",
                    "work_create(items:[{kind:\"" + kind + "\", title:\"…\", …}])"));
            }

            var shortfall = Shortfall(kind, rules, wanted);
            if (shortfall is not null)
            {
                return WriteResult.No(shortfall);
            }

            if (!Blank(wanted.Ref) && wanted.Ref!.Contains('/'))
            {
                return WriteResult.No(Refusal.Blocked(
                    $"work_create(items:[{{kind:\"{kind}\", ref:\"{wanted.Ref.Trim()}\"}}]) — ref contains '/'.",
                    "'/' is the qualifier separator (project/run/ref), so a ref holding one could never be called back",
                    $"work_create(items:[{{kind:\"{kind}\", ref:\"{SlashFree(wanted.Ref)}\", …}}])",
                    $"work_create(items:[{{kind:\"{kind}\", …}}]) without ref — the server numbers it"));
            }

            var repo = wanted.Repo?.Keys() ?? [];
            if (repo.Count > 0 && kind != "project")
            {
                return WriteResult.No(NotAProject("work_create(items:[{repo:…}])", wanted.Title, kind));
            }

            Guid? parentId = null;
            ScopeRefLite? parent = null;

            if (!Blank(wanted.Parent))
            {
                var (found, refusal) = await ResolveAsync(workspaceId, wanted.Parent, cancellationToken);
                if (found is null)
                {
                    return WriteResult.No(refusal!);
                }

                parentId = found.Id;
                var row = await LoadAsync(connection, transaction, found.Id, cancellationToken);
                parent = new ScopeRefLite(row.Id, row.Ref, row.RefScopeId);
            }
            else if (kind is not ("project" or "feature"))
            {
                return WriteResult.No(Refusal.Blocked(
                    $"work_create(items:[{{kind:\"{kind}\"}}]) without a parent.",
                    "everything below a project hangs off something — an orphan is invisible to "
                    + "work_next and to every rollup",
                    $"work_create(items:[{{kind:\"{kind}\", parent:\"<a ref>\", title:\"…\"}}])",
                    "work_tree() to see what exists to hang it from"));
            }

            var refScope = parent is null
                ? null
                : parent.RefScopeId == parent.Id ? parent.Id : parent.RefScopeId;

            var reference = Blank(wanted.Ref)
                ? await DerivedRefAsync(connection, transaction, workspaceId, parent, refScope, kind, cancellationToken)
                : wanted.Ref!.Trim();

            var row2 = new NewRow(
                Guid.CreateVersion7(),
                workspaceId,
                parentId,
                kind,
                reference,
                await NextSeqAsync(connection, transaction, parentId, cancellationToken),
                Glyph.Flat(wanted.Title),
                wanted.Intent,
                wanted.VerifyCmd,
                wanted.RiskTags,
                (short)Math.Clamp(wanted.Priority ?? 0, -9, 9));

            try
            {
                await InsertItemAsync(connection, transaction, row2, cancellationToken);
            }
            catch (PostgresException refused)
            {
                return WriteResult.No(FromPostgres(refused, $"work_create(items:[{{ref:\"{reference}\"}}])"));
            }

            await CriteriaAsync(connection, transaction, row2.Id, wanted.Criteria ?? [], cancellationToken);

            var restored = await UnarchiveForNewWorkAsync(
                connection, transaction, workspaceId, row2.Id, reference, actor, cancellationToken);

            if (repo.Count > 0)
            {
                var (_, taken) = await BindReposAsync(
                    connection, transaction, workspaceId, row2.Id, reference, repo, cancellationToken);
                if (taken is not null)
                {
                    return WriteResult.No(taken);
                }
            }

            await EventAsync(
                connection, transaction, workspaceId, row2.Id, actor, "created",
                $"{reference} ({kind}) created under {parent?.Ref ?? "—"}: {Glyph.Flat(wanted.Title)}",
                repo.Count > 0
                    ? (object)new { kind, parent = parent?.Ref, title = wanted.Title, repo = repo.Select(k => k.Key).ToArray() }
                    : new { kind, parent = parent?.Ref, title = wanted.Title },
                cancellationToken);

            var tail = $"{Glyph.Pad(Glyph.Kind(kind), 6)}  " + Glyph.Row(wanted.Title, 60);
            created.Add((written.Count, row2.Id, reference, tail));
            written.Add($"{Glyph.Pad(reference, 8)}  {tail}");

            if (repo.Count > 0)
            {
                written.Add($"          {RepoLine(repo)}");
            }

            written.AddRange(restored.Select(r => $"          project {r} unarchived — new work arrived"));
        }

        await transaction.CommitAsync(cancellationToken);

        // doc 13: printed refs must call back. Uniqueness is only checkable once the batch is
        // visible to another connection, so the rows are re-rendered after the commit.
        var firstRef = string.Empty;
        foreach (var (line, id, reference, tail) in created)
        {
            var printed = await board.PrintableRefAsync(workspaceId, id, reference, cancellationToken);
            written[line] = $"{Glyph.Pad(printed, 8)}  {tail}";
            firstRef = firstRef.Length == 0 ? printed : firstRef;
        }

        return WriteResult.Ok(string.Join(
            "\n",
            [
                $"OK  {written.Count} item(s) created",
                .. written.Select(w => $"    {w}"),
                string.Empty,
                $"Next: work_link if any of these depends on another, then work_next() — "
                + $"the frontier has changed. work_status(ref:\"{firstRef}\", to:\"in_progress\") to start.",
            ]));
    }

    /// <summary>
    /// doc 01 §3.2 — a field-level patch with <c>expected_revision</c>, so a concurrent edit is
    /// reported with what actually changed rather than silently overwritten.
    /// </summary>
    public async Task<WriteResult> UpdateAsync(
        Guid workspaceId,
        Actor actor,
        string? reference,
        UpdateArgs args,
        CancellationToken cancellationToken = default)
    {
        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        var repo = args.Repo?.Keys() ?? [];

        var patching = Fields(args).Length > 0 || args.RiskTags is not null || args.Criteria is { Length: > 0 } || repo.Count > 0;

        if (!patching && args.Archived is null)
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_update({scope.Ref}) — nothing changed: the call sets no field and no criterion.",
                "every field it can patch was absent or blank, so there was nothing to write",
                $"work_update(ref:\"{reference}\", criteria:[{{id:\"c1\", checked:true, evidence:\"…\"}}]) "
                + "to tick a criterion",
                $"work_update(ref:\"{reference}\", title:\"…\") — or intent, verify_cmd, risk_tags, priority"));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);

        if (args.ExpectedRevision is { } expected && expected != item.Revision)
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_update({item.Ref}) — expected_revision {expected}, but it is now {item.Revision}.",
                "someone else wrote to this item since you read it",
                $"work_get(refs:[\"{item.Ref}\"]) to see it as it stands, then retry with "
                + $"expected_revision:{item.Revision}"));
        }

        var unwritable = await CriteriaRefusalAsync(
            connection, transaction, item, args.Criteria ?? [], cancellationToken);
        if (unwritable is not null)
        {
            return WriteResult.No(unwritable);
        }

        IReadOnlyList<RepoKey> bound = [];
        if (repo.Count > 0)
        {
            if (item.Kind != "project")
            {
                return WriteResult.No(NotAProject($"work_update({item.Ref})", item.Ref, item.Kind));
            }

            var (added, taken) = await BindReposAsync(
                connection, transaction, item.WorkspaceId, item.Id, item.Ref, repo, cancellationToken);
            if (taken is not null)
            {
                return WriteResult.No(taken);
            }

            bound = added;
        }

        if (args.Archived is not null && item.Kind != "project")
        {
            return WriteResult.No(NotArchivable(item.Ref, item.Kind));
        }

        var touched = 0;
        if (patching)
        {
            const string sql =
                """
                UPDATE item
                   SET title      = coalesce(@title, title),
                       intent     = coalesce(@intent, intent),
                       verify_cmd = coalesce(@verify, verify_cmd),
                       risk_tags  = coalesce(@risk, risk_tags),
                       priority   = coalesce(@priority, priority),
                       revision   = revision + 1
                 WHERE id = @id;
                """;

            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                Bind(command, "id", item.Id);
                Bind(command, "title", Blank(args.Title) ? null : Glyph.Flat(args.Title));
                Bind(command, "intent", Blank(args.Intent) ? null : args.Intent);
                Bind(command, "verify", Blank(args.VerifyCmd) ? null : args.VerifyCmd);
                Bind(command, "risk", args.RiskTags);
                Bind(command, "priority", args.Priority is { } p ? (short)Math.Clamp(p, -9, 9) : null);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            touched = await PatchCriteriaAsync(
                connection, transaction, item.Id, args.Criteria ?? [], cancellationToken);

            await HeartbeatAsync(
                connection,
                transaction,
                item.Id,
                actor,
                await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken),
                cancellationToken);

            await EventAsync(
                connection, transaction, item.WorkspaceId, item.Id, actor, "updated",
                $"{item.Ref} updated" + (touched > 0 ? $", {touched} criterion(s)" : string.Empty),
                repo.Count > 0
                    ? (object)new { fields = Fields(args), criteria = touched, repo = bound.Select(k => k.Key).ToArray() }
                    : new { fields = Fields(args), criteria = touched },
                cancellationToken);
        }

        var archiveLine = string.Empty;
        if (args.Archived is { } archived)
        {
            var (_, changed) = await ArchiveRowAsync(
                connection, transaction, item.WorkspaceId, item.Id, item.Ref, archived, actor, "work_update",
                cancellationToken);

            archiveLine = (archived, changed) switch
            {
                (true, true) => "    archived — hidden from the dashboard's lists; its ref and repo still open it",
                (true, false) => "    already archived",
                (false, true) => "    unarchived — back in the dashboard's lists",
                _ => "    not archived — nothing to restore",
            };
        }

        await transaction.CommitAsync(cancellationToken);

        var already = repo.Where(k => !bound.Contains(k)).ToList();
        return WriteResult.Ok(string.Join(
            "\n",
            [
                patching
                    ? $"OK  {item.Ref}  updated (revision {item.Revision + 1})"
                      + (touched > 0 ? $", {touched} criterion(s) written" : string.Empty)
                    : $"OK  {item.Ref}",
                .. bound.Count > 0 ? [$"    {RepoLine(bound)} — bound"] : Array.Empty<string>(),
                .. already.Count > 0 ? [$"    {RepoLine(already)} — already this project's"] : Array.Empty<string>(),
                .. archiveLine.Length > 0 ? [archiveLine] : Array.Empty<string>(),
                string.Empty,
                !patching
                    ? $"Next: work_brief(scope:\"{item.Ref}\") still opens it; work_create under it unarchives it."
                    : repo.Count > 0 && Fields(args).Length == 0 && args.Criteria is not { Length: > 0 }
                    ? $"Next: work_brief(repo:{RepoKeys.Literal(repo)}) — a session in that repo now opens {item.Ref}."
                    : $"Next: work_status(ref:\"{item.Ref}\", to:\"done\") when the criteria are met — "
                      + "the guard will tell you what is still missing.",
            ]));
    }

    /// <summary>
    /// doc 01 §3.2 — separate from <c>update</c> because it is the risky one. Fractional
    /// <c>seq</c> means inserting between two siblings rewrites one row, not the tail of the plan.
    /// </summary>
    public async Task<WriteResult> MoveAsync(
        Guid workspaceId,
        Actor actor,
        string? reference,
        string? parent,
        string? before,
        string? after,
        CancellationToken cancellationToken = default)
    {
        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        if (Blank(parent) && Blank(before) && Blank(after))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_move({scope.Ref}) with nowhere to move it.",
                "a move needs a new parent, or a sibling to sit before or after",
                $"work_move(ref:\"{scope.Ref}\", parent:\"<a ref>\")",
                $"work_move(ref:\"{scope.Ref}\", before:\"<a sibling ref>\")"));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);
        var anchorRef = Blank(before) ? after : before;
        Guid? parentId = item.ParentId;
        decimal seq;

        if (!Blank(anchorRef))
        {
            var (anchor, refusal) = await ResolveAsync(workspaceId, anchorRef, cancellationToken);
            if (anchor is null)
            {
                return WriteResult.No(refusal!);
            }

            var anchorRow = await LoadAsync(connection, transaction, anchor.Id, cancellationToken);
            parentId = anchorRow.ParentId;
            seq = await BetweenAsync(
                connection, transaction, anchorRow.Id, parentId, !Blank(before), cancellationToken);
        }
        else
        {
            var (target, refusal) = await ResolveAsync(workspaceId, parent, cancellationToken);
            if (target is null)
            {
                return WriteResult.No(refusal!);
            }

            parentId = target.Id;
            seq = await NextSeqAsync(connection, transaction, parentId, cancellationToken);
        }

        const string sql = "UPDATE item SET parent_id = @parent, seq = @seq, revision = revision + 1 WHERE id = @id;";

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { id = item.Id, parent = parentId, seq },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException refused)
        {
            return WriteResult.No(FromPostgres(refused, $"work_move(ref:\"{item.Ref}\")"));
        }

        await EventAsync(
            connection, transaction, item.WorkspaceId, item.Id, actor, "moved",
            $"{item.Ref} moved to seq {seq}",
            new { parent, before, after, seq },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return WriteResult.Ok(string.Join(
            "\n",
            $"OK  {item.Ref}  moved, seq {seq}",
            string.Empty,
            $"Next: work_tree(scope:\"{parent ?? item.Ref}\") to confirm the shape."));
    }

    /// <summary>
    /// doc 01 §3.2. The <c>blocks</c> cycle check and the <c>supersedes</c> side effect are U1
    /// triggers — this writes the row and surfaces what Postgres says if it refuses (doc 07 §1).
    /// </summary>
    public async Task<WriteResult> LinkAsync(
        Guid workspaceId,
        Actor actor,
        string? from,
        string? to,
        string? type,
        bool remove,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<LinkType>(type?.Replace("_", string.Empty).Trim(), ignoreCase: true, out var linkType)
            || !Enum.IsDefined(linkType))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_link(type:\"{type}\") — \"{type}\" is not a link type.",
                "five types, and each means something different to the frontier",
                "work_link(from:\"2.4\", to:\"2.5\", type:\"blocks\") — also supersedes, duplicates, "
                + "derived_from, verifies"));
        }

        var (source, sourceRefusal) = await ResolveAsync(workspaceId, from, cancellationToken);
        if (source is null)
        {
            return WriteResult.No(sourceRefusal!);
        }

        var (target, targetRefusal) = await ResolveAsync(workspaceId, to, cancellationToken);
        if (target is null)
        {
            return WriteResult.No(targetRefusal!);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (remove)
        {
            const string sql =
                """
                DELETE FROM item_link
                 WHERE from_item_id = @from AND to_item_id = @to AND type = @type::link_type;
                """;

            var deleted = await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { from = source.Id, to = target.Id, type = Pg.Label(linkType) },
                transaction,
                cancellationToken: cancellationToken));

            if (deleted == 0)
            {
                return WriteResult.No(Refusal.Blocked(
                    $"work_link(from:\"{source.Ref}\", to:\"{target.Ref}\", type:\"{Pg.Label(linkType)}\", "
                    + "remove:true) — there is no such link.",
                    "nothing was removed, so nothing changed",
                    $"work_get(refs:[\"{source.Ref}\"], include:\"deps\") to see the links that exist"));
            }

            await EventAsync(
                connection, transaction, workspaceId, target.Id, actor, "unlinked",
                $"{source.Ref} -{Pg.Label(linkType)}-> {target.Ref} removed",
                new { from = source.Ref, to = target.Ref, type = Pg.Label(linkType) },
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return WriteResult.Ok(string.Join(
                "\n",
                $"OK  {source.Ref} -{Pg.Label(linkType)}-> {target.Ref} removed",
                string.Empty,
                "Next: work_next() — the frontier has just changed."));
        }

        try
        {
            await LinkAsync(
                connection, transaction, workspaceId, source.Id, target.Id, linkType, actor, cancellationToken);
        }
        catch (PostgresException refused)
        {
            return WriteResult.No(FromPostgres(
                refused, $"work_link(from:\"{source.Ref}\", to:\"{target.Ref}\", type:\"{Pg.Label(linkType)}\")"));
        }

        await EventAsync(
            connection, transaction, workspaceId, target.Id, actor, "linked",
            $"{source.Ref} -{Pg.Label(linkType)}-> {target.Ref}",
            new { from = source.Ref, to = target.Ref, type = Pg.Label(linkType) },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return WriteResult.Ok(string.Join(
            "\n",
            $"OK  {source.Ref} -{Pg.Label(linkType)}-> {target.Ref}"
            + (linkType == LinkType.Supersedes ? $"; {target.Ref} is now superseded" : string.Empty),
            string.Empty,
            "Next: work_next() — the frontier has just changed."));
    }

    private static string SlashFree(string reference) => reference.Trim().Trim('/').Replace('/', '-');

    private sealed record ScopeRefLite(Guid Id, string Ref, Guid? RefScopeId);

    private sealed record KindRules(bool RequiresVerify, bool RequiresCriteria, bool ScopesRefs);

    /// <summary>
    /// doc 01 §3.2 — the one creation rule that is a guard rather than a constraint, because it is
    /// about whether the brief is answerable, not about whether the row is well formed.
    /// </summary>
    private static string? Shortfall(string kind, KindRules rules, NewItemArg wanted)
    {
        var unmet = new List<Unmet>();

        if (rules.RequiresVerify && Blank(wanted.VerifyCmd))
        {
            unmet.Add(new Unmet(
                $"a {kind} without a verify_cmd cannot be closed honestly — nothing would judge it",
                $"work_create(items:[{{kind:\"{kind}\", title:\"{Glyph.Flat(wanted.Title)}\", "
                + "verify_cmd:\"dotnet test --filter …\", criteria:[\"…\"]}]) — and it must be a "
                + "command that is RED before the work"));
        }

        if (rules.RequiresCriteria && (wanted.Criteria is null || wanted.Criteria.Length == 0))
        {
            unmet.Add(new Unmet(
                $"a {kind} without done-criteria is a brief nobody can check off",
                $"work_create(items:[{{kind:\"{kind}\", title:\"{Glyph.Flat(wanted.Title)}\", "
                + "criteria:[\"the observable thing that is true when this is done\"]}])"));
        }

        return unmet.Count == 0
            ? null
            : Refusal.Blocked(
                $"work_create(items:[{{kind:\"{kind}\", title:\"{Glyph.Flat(wanted.Title)}\"}}]). "
                + $"{unmet.Count} requirement(s) unmet.",
                unmet);
    }

    private static async Task<Dictionary<string, KindRules>> LadderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT key                AS "Key",
                   requires_verify    AS "RequiresVerify",
                   requires_criteria  AS "RequiresCriteria",
                   scopes_refs        AS "ScopesRefs"
            FROM   item_kind;
            """;

        var rows = await connection.QueryAsync(new CommandDefinition(
            sql, transaction: transaction, cancellationToken: cancellationToken));

        return rows.ToDictionary(
            r => (string)r.Key,
            r => new KindRules((bool)r.RequiresVerify, (bool)r.RequiresCriteria, (bool)r.ScopesRefs),
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> DerivedRefAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        ScopeRefLite? parent,
        Guid? refScopeId,
        string kind,
        CancellationToken cancellationToken)
    {
        if (parent is null)
        {
            return await FreeRefAsync(
                connection, transaction, workspaceId, refScopeId, n => $"{kind}-{n}", cancellationToken);
        }

        // A run or a project scopes its own refs, so its children number from 1 rather than
        // inheriting a prefix nobody types: `2.3`, not `entity-store.3`.
        var prefix = parent.RefScopeId == parent.Id ? string.Empty : $"{parent.Ref}.";

        return await FreeRefAsync(
            connection, transaction, workspaceId, refScopeId, n => $"{prefix}{n}", cancellationToken);
    }

    private static async Task CriteriaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        IReadOnlyList<string> criteria,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO item_criterion (id, item_id, seq, text, checked)
            VALUES (@id, @item, @seq, @text, false);
            """;

        for (var i = 0; i < criteria.Count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql,
                new { id = Guid.CreateVersion7(), item = itemId, seq = i + 1, text = criteria[i] },
                transaction,
                cancellationToken: cancellationToken));
        }
    }

    /// <summary>
    /// doc 01 §3.2 — an id addresses a criterion that exists; a text with no id appends. A row that
    /// fits neither would write nothing, and an <c>OK</c> over a write that did not happen is the
    /// silent drop this whole server exists to prevent — so the whole patch is refused before
    /// anything is written, naming the ids that exist and the form that appends.
    /// </summary>
    private static async Task<string?> CriteriaRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        IReadOnlyList<CriterionArg> criteria,
        CancellationToken cancellationToken)
    {
        if (criteria.Count == 0)
        {
            return null;
        }

        const string sql = "SELECT seq FROM item_criterion WHERE item_id = @id ORDER BY seq;";

        var existing = (await connection.QueryAsync<int>(new CommandDefinition(
            sql, new { id = item.Id }, transaction, cancellationToken: cancellationToken))).AsList();

        var known = existing.Count == 0
            ? "it has no criteria yet"
            : $"it has {string.Join(", ", existing.Select(s => $"c{s}"))}";

        var unmet = new List<Unmet>();

        foreach (var criterion in criteria)
        {
            var text = Blank(criterion.Text) ? "…" : Glyph.Flat(criterion.Text);
            var append = $"work_update(ref:\"{item.Ref}\", criteria:[{{text:\"{text}\"}}]) appends it as "
                         + $"c{(existing.Count == 0 ? 1 : existing.Max() + 1)} — the server numbers it";

            if (Blank(criterion.Id))
            {
                if (Blank(criterion.Text))
                {
                    unmet.Add(new Unmet(
                        "a criterion with neither an id nor a text addresses nothing",
                        [append, $"work_update(ref:\"{item.Ref}\", criteria:[{{id:\"c1\", checked:true, "
                                 + "evidence:\"…\"}]) to change one that exists"]));
                }

                continue;
            }

            if (Seq(criterion.Id) is not { } seq || !existing.Contains(seq))
            {
                unmet.Add(new Unmet(
                    $"criterion \"{Glyph.Flat(criterion.Id)}\" does not exist on {item.Ref} — {known}; "
                    + "an id addresses an existing criterion, it never creates one",
                    [
                        append,
                        $"work_get(refs:[\"{item.Ref}\"]) to see the criteria that exist and their ids",
                    ]));
                continue;
            }

            if (criterion.Checked is null && Blank(criterion.Text) && Blank(criterion.Evidence))
            {
                unmet.Add(new Unmet(
                    $"criterion c{seq} is addressed but given nothing to write",
                    $"work_update(ref:\"{item.Ref}\", criteria:[{{id:\"c{seq}\", checked:true, evidence:\"…\"}}])"));
            }
        }

        return unmet.Count == 0
            ? null
            : Refusal.Blocked(
                $"work_update({item.Ref}) — {unmet.Count} criterion row(s) would write nothing. Nothing was written.",
                unmet);
    }

    private static async Task<int> PatchCriteriaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        IReadOnlyList<CriterionArg> criteria,
        CancellationToken cancellationToken)
    {
        const string update =
            """
            UPDATE item_criterion
               SET checked    = coalesce(@checked, checked),
                   text       = coalesce(@text, text),
                   evidence   = coalesce(@evidence, evidence),
                   checked_at = CASE WHEN coalesce(@checked, checked) THEN coalesce(checked_at, now()) END
             WHERE item_id = @item AND seq = @seq;
            """;

        const string insert =
            """
            INSERT INTO item_criterion (id, item_id, seq, text, checked, evidence, checked_at)
            SELECT @id, @item, coalesce(max(seq), 0) + 1, @text, coalesce(@checked, false), @evidence,
                   CASE WHEN coalesce(@checked, false) THEN now() END
            FROM   item_criterion WHERE item_id = @item;
            """;

        var touched = 0;

        foreach (var criterion in criteria)
        {
            var seq = Seq(criterion.Id);

            if (seq is { } number)
            {
                touched += await connection.ExecuteAsync(new CommandDefinition(
                    update,
                    new
                    {
                        item = itemId,
                        seq = number,
                        @checked = criterion.Checked,
                        text = Blank(criterion.Text) ? null : criterion.Text,
                        evidence = Blank(criterion.Evidence) ? null : criterion.Evidence,
                    },
                    transaction,
                    cancellationToken: cancellationToken));

                continue;
            }

            if (Blank(criterion.Text))
            {
                continue;
            }

            touched += await connection.ExecuteAsync(new CommandDefinition(
                insert,
                new
                {
                    id = Guid.CreateVersion7(),
                    item = itemId,
                    text = criterion.Text,
                    @checked = criterion.Checked,
                    evidence = Blank(criterion.Evidence) ? null : criterion.Evidence,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        return touched;
    }

    /// <summary><c>c2</c> is criterion seq 2 — the form every refusal and every board row prints.</summary>
    private static int? Seq(string? id)
    {
        if (Blank(id))
        {
            return null;
        }

        var digits = id!.Trim().TrimStart('c', 'C');
        return int.TryParse(digits, out var seq) && seq > 0 ? seq : null;
    }

    private static async Task<decimal> BetweenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid anchorId,
        Guid? parentId,
        bool before,
        CancellationToken cancellationToken)
    {
        var sql = before
            ? """
              SELECT coalesce(
                (SELECT max(s.seq) FROM item s
                  WHERE s.parent_id IS NOT DISTINCT FROM @parent AND s.seq < a.seq), a.seq - 2) / 2
                + a.seq / 2
              FROM item a WHERE a.id = @anchor;
              """
            : """
              SELECT coalesce(
                (SELECT min(s.seq) FROM item s
                  WHERE s.parent_id IS NOT DISTINCT FROM @parent AND s.seq > a.seq), a.seq + 2) / 2
                + a.seq / 2
              FROM item a WHERE a.id = @anchor;
              """;

        return await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            sql,
            new { anchor = anchorId, parent = parentId },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static string[] Fields(UpdateArgs args)
    {
        var fields = new List<string>();
        if (!Blank(args.Title)) fields.Add("title");
        if (!Blank(args.Intent)) fields.Add("intent");
        if (!Blank(args.VerifyCmd)) fields.Add("verify_cmd");
        if (args.RiskTags is { Length: > 0 }) fields.Add("risk_tags");
        if (args.Priority is not null) fields.Add("priority");
        return [.. fields];
    }

    /// <summary>
    /// doc 07 §1 — a rule whose violation should be impossible lives in Postgres, and its message
    /// already names the fix. Surfacing it beats paraphrasing it in C# and drifting from it.
    /// </summary>
    private static string FromPostgres(PostgresException refused, string call)
    {
        var routes = new List<string>();
        if (!Blank(refused.Hint))
        {
            routes.Add(refused.Hint!);
        }

        routes.Add("work_tree() to see the shape this has to fit");

        return Refusal.Blocked(
            $"{call} — the schema refused it.",
            Glyph.Flat(refused.MessageText),
            [.. routes]);
    }

    /// <summary>
    /// The transport's last line (doc 04 §3). A guard no writer path catches itself — in practice a
    /// concurrent write taking a ref, seq or claim between this call's check and its insert — still
    /// comes back as a refusal with a route, never as "An error occurred".
    /// </summary>
    public static string Uncaught(PostgresException refused, string call)
    {
        var routes = new List<string>
        {
            $"{call} again — a concurrent write can take a ref, a seq or a claim between this call's "
            + "check and its insert, and the retry reads the new state",
        };

        if (!Blank(refused.Hint))
        {
            routes.Add(refused.Hint!);
        }

        routes.Add("work_brief() to see the state as it stands now");

        return Refusal.Blocked(
            $"{call} — the schema refused it.",
            Glyph.Flat(refused.MessageText),
            [.. routes]);
    }
}
