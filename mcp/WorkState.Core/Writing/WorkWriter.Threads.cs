using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>One row of <c>work_finding</c>'s batch — doc 01 §3.4.</summary>
public sealed record NewFinding(
    [property: System.Text.Json.Serialization.JsonPropertyName("target")] string Target,
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("severity")] string? Severity = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("source")] string? Source = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("intent")] string? Intent = null);

public sealed partial class WorkWriter
{
    /// <summary>
    /// doc 01 §3.4. A note addressed to an item is injected into that item's brief and marked
    /// consumed there, which is why <c>to:</c> must resolve: a note addressed to nowhere is the
    /// carry-forward that never carried.
    /// </summary>
    public async Task<WriteResult> NoteAsync(
        Guid workspaceId,
        Actor actor,
        string? kind,
        string? body,
        string? to,
        string? from,
        CancellationToken cancellationToken = default)
    {
        if (Blank(body))
        {
            return WriteResult.No(Refusal.Blocked(
                "work_note without a body.",
                "the body is the note",
                "work_note(kind:\"carry_forward\", body:\"…\", to:\"<a ref>\")"));
        }

        if (!Enum.TryParse<NoteKind>(kind?.Replace("_", string.Empty).Trim(), ignoreCase: true, out var noteKind)
            || !Enum.IsDefined(noteKind))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_note(kind:\"{kind}\") — \"{kind}\" is not a note kind.",
                "kind says how the next reader should treat it",
                "work_note(kind:\"carry_forward\", body:\"…\") — also do_not_rederive, correction, "
                + "incident, environment, decision, observation"));
        }

        Guid? toItemId = null;

        if (!Blank(to))
        {
            var (target, refusal) = await ResolveAsync(workspaceId, to, cancellationToken);
            if (target is null && to!.Contains(':', StringComparison.Ordinal))
            {
                return WriteResult.No(Refusal.Blocked(
                    $"work_note(to:\"{Glyph.Flat(to)}\") — \"{Glyph.Flat(to)}\" is a selector, not a ref.",
                    "a note is delivered only to one item's brief; a selector would be stored and never delivered",
                    "work_note(kind:\"…\", body:\"…\", to:\"<a ref>\")",
                    "work_get(refs:[…]) or work_tree() to find the ref"));
            }

            if (target is null)
            {
                return WriteResult.No(refusal!);
            }

            toItemId = target.Id;
        }

        Guid? fromItemId = null;
        if (!Blank(from))
        {
            var (source, _) = await ResolveAsync(workspaceId, from, cancellationToken);
            fromItemId = source?.Id;
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        await InsertNoteAsync(
            connection, transaction, workspaceId, actor, noteKind, body!, fromItemId, toItemId, cancellationToken);

        await EventAsync(
            connection,
            transaction,
            workspaceId,
            toItemId ?? fromItemId,
            actor,
            "note",
            $"{Pg.Label(noteKind)} note{(Blank(to) ? string.Empty : $" to {to}")}: {Glyph.Flat(body)}",
            new { kind = Pg.Label(noteKind), to, body },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return WriteResult.Ok(string.Join(
            "\n",
            $"OK  note recorded ({Pg.Label(noteKind)})"
            + (Blank(to) ? string.Empty : $", addressed to {to}"),
            $"    {Glyph.Row(body, 92)}",
            string.Empty,
            Blank(to)
                ? "Next: nothing — a note is a side effect, not a step."
                : $"Next: nothing — it renders into work_brief(scope:\"{to}\", for:\"worker\") and is "
                  + "marked consumed there."));
    }

    /// <summary>Shared by <c>work_note</c> and <c>work_import</c>'s note records.</summary>
    private static async Task<Guid> InsertNoteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Actor actor,
        NoteKind kind,
        string body,
        Guid? fromItemId,
        Guid? toItemId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO note (id, workspace_id, from_item_id, kind, body, to_item_id, created_by, session_id)
            VALUES (@id, @workspace, @from, @kind::note_kind, @body, @to, @actor, @session);
            """;

        var id = Guid.CreateVersion7();

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id,
                session = actor.SessionId,
                workspace = workspaceId,
                from = fromItemId,
                kind = Pg.Label(kind),
                body = body.Trim(),
                to = toItemId,
                actor = actor.Name,
            },
            transaction,
            cancellationToken: cancellationToken));

        return id;
    }

    /// <summary>
    /// doc 01 §3.4 — create or resolve. On resolve the <c>reversal</c> cost is cleared: the column
    /// is nullable and nothing in the schema enforces "resolved ⇒ null" (U2c), so making this the
    /// only writer of the transition is what makes a non-null reversal on a resolved row mean
    /// something — an imported row, not a live one.
    /// </summary>
    public async Task<WriteResult> AssumeAsync(
        Guid workspaceId,
        Actor actor,
        string? reference,
        string? statement,
        string? evidence,
        string? reversal,
        string? revisitWhen,
        string? resolve,
        string? resolutionEvidence,
        CancellationToken cancellationToken = default)
    {
        if (!Blank(resolve))
        {
            return await ResolveAssumptionAsync(
                workspaceId, actor, reference, resolve!, resolutionEvidence, cancellationToken);
        }

        if (Blank(statement))
        {
            return WriteResult.No(Refusal.Blocked(
                "work_assume without a statement and without a resolve.",
                "an assumption is either being opened (statement, evidence, reversal, revisit_when) "
                + "or being closed (ref, resolve, evidence)",
                "work_assume(statement:\"…\", evidence:\"reasoned\", reversal:\"medium\", "
                + "revisit_when:\"<a ref>\", scope:\"<a ref>\")",
                "work_assume(ref:\"A3\", resolve:\"confirmed\", evidence:\"…\")"));
        }

        if (!Level(evidence, out var level))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_assume(evidence:\"{evidence}\") — \"{evidence}\" is not an evidence level.",
                "the level is what a later reader uses to decide whether to re-derive this",
                "work_assume(statement:\"…\", evidence:\"reasoned\", reversal:\"medium\", "
                + "revisit_when:\"…\") — also asserted, code_read, probed, measured"));
        }

        if (!Cost(reversal, out var cost))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_assume(reversal:\"{reversal}\") — \"{reversal}\" is not a reversal cost.",
                "the cost of being wrong is what decides whether this is worth probing now",
                "work_assume(statement:\"…\", evidence:\"reasoned\", reversal:\"medium\", "
                + "revisit_when:\"…\") — also trivial, low, high, irreversible"));
        }

        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        Guid? revisit = null;
        if (!Blank(revisitWhen))
        {
            var (target, refusal) = await ResolveAsync(workspaceId, revisitWhen, cancellationToken);
            if (target is null)
            {
                return WriteResult.No(refusal!);
            }

            revisit = target.Id;
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);
        var opened = await AssumeAsync(
            connection, transaction, item, actor, statement!.Trim(), level, cost, revisit, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return WriteResult.Ok(string.Join(
            "\n",
            $"OK  {opened}  assumption opened, scoped to {item.Ref}",
            $"    {Glyph.Row(statement, 92)}",
            $"    {Pg.Label(level)} · reversal {Pg.Label(cost)}"
            + (Blank(revisitWhen) ? string.Empty : $" · revisit when {revisitWhen} closes"),
            string.Empty,
            Blank(revisitWhen)
                ? "Next: nothing — it stays in work_brief until resolved."
                : $"Next: nothing — it surfaces in attention the moment {revisitWhen} closes."));
    }

    /// <summary>
    /// doc 01 §3.4 — findings from a review, gate or audit, as real items. A <c>blocker</c> under a
    /// phase blocks that phase's <c>done</c> transition, which is the gate finding that used to
    /// dissolve into prose (doc 07 §3 condition 4).
    /// </summary>
    public async Task<WriteResult> FindingAsync(
        Guid workspaceId,
        Actor actor,
        IReadOnlyList<NewFinding> findings,
        CancellationToken cancellationToken = default)
    {
        if (findings.Count == 0)
        {
            return WriteResult.No(Refusal.Blocked(
                "work_finding with no findings.",
                "it takes an array, because a review produces several at once",
                "work_finding(findings:[{target:\"2.4\", title:\"…\", severity:\"blocker\", "
                + "source:\"review\"}])"));
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var written = new List<string>();

        foreach (var finding in findings)
        {
            if (Blank(finding.Title) || Blank(finding.Target))
            {
                return WriteResult.No(Refusal.Blocked(
                    "work_finding needs a target and a title on every row.",
                    "a finding that names no target is the ⚠ marker this replaces",
                    "work_finding(findings:[{target:\"2.4\", title:\"…\", severity:\"blocker\", "
                    + "source:\"review\"}])"));
            }

            var (target, refusal) = await ResolveAsync(workspaceId, finding.Target, cancellationToken);
            if (target is null)
            {
                return WriteResult.No(refusal!);
            }

            var parent = await LoadAsync(connection, transaction, target.Id, cancellationToken);
            var severity = finding.Severity?.Trim().ToLowerInvariant() is "blocker" ? "blocker" : "minor";

            var row = new NewRow(
                Guid.CreateVersion7(),
                workspaceId,
                parent.Id,
                "finding",
                await FreeRefAsync(
                    connection, transaction, workspaceId, parent.RefScopeId, n => $"F-{n:00}", cancellationToken),
                await NextSeqAsync(connection, transaction, parent.Id, cancellationToken),
                Glyph.Flat(finding.Title),
                finding.Intent,
                Severity: severity,
                Source: Blank(finding.Source) ? "review" : finding.Source!.Trim().ToLowerInvariant());

            await InsertItemAsync(connection, transaction, row, cancellationToken);

            await EventAsync(
                connection,
                transaction,
                workspaceId,
                row.Id,
                actor,
                "finding",
                $"{row.Ref} ({severity}) against {parent.Ref}: {Glyph.Flat(finding.Title)}",
                new { target = parent.Ref, severity, source = row.Source },
                cancellationToken);

            written.Add($"{Glyph.Pad(row.Ref, 6)}  {Glyph.Pad(severity, 7)}  → {parent.Ref}  "
                      + Glyph.Row(finding.Title, 56));
        }

        await transaction.CommitAsync(cancellationToken);

        var first = findings[0];

        return WriteResult.Ok(string.Join(
            "\n",
            [
                $"OK  {written.Count} finding(s) recorded",
                .. written.Select(w => $"    {w}"),
                string.Empty,
                $"Next: work_status(ref:\"{first.Target}\", to:\"in_progress\") to act on one, or "
                + "work_status(ref:\"<the finding>\", to:\"deferred\", target:\"backlog\", reason:\"…\").",
            ]));
    }

    private async Task<WriteResult> ResolveAssumptionAsync(
        Guid workspaceId,
        Actor actor,
        string? reference,
        string resolve,
        string? evidence,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AssumptionState>(resolve.Trim(), ignoreCase: true, out var state)
            || state == AssumptionState.Open
            || !Enum.IsDefined(state))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_assume(resolve:\"{resolve}\") — \"{resolve}\" is not a resolution.",
                "an assumption closes as one of three things",
                $"work_assume(ref:\"{reference}\", resolve:\"confirmed\", evidence:\"…\") — also "
                + "disproven, obsolete"));
        }

        if (Blank(reference))
        {
            return WriteResult.No(Refusal.Blocked(
                "work_assume(resolve:…) without a ref.",
                "resolving needs to know which assumption",
                "work_threads(include:\"assumptions\") lists every open one with its ref"));
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var matches = await OpenAssumptionsAsync(
            connection, transaction, workspaceId, reference!.Trim(), cancellationToken);

        if (matches.Count == 0)
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_assume(ref:\"{reference}\", resolve:\"{resolve}\") — no open assumption has that ref.",
                "it is already resolved, or the ref is from another run — qualify it with the run, "
                + "e.g. \"entity-store/A1\"",
                "work_threads(include:\"assumptions\") lists every open one with its ref"));
        }

        if (matches.Count > 1)
        {
            // ws/13: a form is offered only if it resolves to that assumption alone — a
            // workspace-level A1 has no qualified form, and its bare ref is the ambiguous one.
            var forms = new List<string>();
            foreach (var match in matches.Take(4))
            {
                var resolved = await OpenAssumptionsAsync(
                    connection, transaction, workspaceId, match.QualifiedRef, cancellationToken);

                if (resolved is [var only] && only.Id == match.Id)
                {
                    forms.Add(match.QualifiedRef);
                }
            }

            var routes = forms
                .Select(f => $"work_assume(ref:\"{f}\", resolve:\"{Pg.Label(state)}\", evidence:\"…\")")
                .ToList();

            if (forms.Count < matches.Count)
            {
                routes.Add("work_threads(include:\"assumptions\") to see the rest — no ref names only them");
            }

            return WriteResult.No(Refusal.Blocked(
                $"work_assume(ref:\"{reference}\", resolve:\"{resolve}\") — \"{reference}\" matches "
                + $"{matches.Count} open assumptions across runs.",
                "an assumption ref is numbered within its run; this one exists in more than one",
                [.. routes]));
        }

        const string sql =
            """
            UPDATE assumption
               SET state           = @state::assumption_state,
                   resolution_note = @note,
                   resolved_at     = now(),
                   reversal        = NULL
             WHERE id = @id AND state = 'open';
            """;

        var closed = matches[0].QualifiedRef;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = matches[0].Id,
                state = Pg.Label(state),
                note = Blank(evidence) ? null : evidence!.Trim(),
            },
            transaction,
            cancellationToken: cancellationToken));

        await EventAsync(
            connection,
            transaction,
            workspaceId,
            null,
            actor,
            "assumption",
            $"{closed} resolved {Pg.Label(state)}: {Glyph.Flat(evidence)}",
            new { @ref = closed, resolve = Pg.Label(state), evidence },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return WriteResult.Ok(string.Join(
            "\n",
            $"OK  {closed}  resolved {Pg.Label(state)}; reversal cost cleared — a settled question has none",
            Blank(evidence) ? string.Empty : $"    {Glyph.Row(evidence, 92)}",
            string.Empty,
            "Next: nothing, unless resolving it unblocks an item — then work_next().").TrimEnd());
    }

    private sealed record OpenAssumption(Guid Id, string Ref, string QualifiedRef);

    /// <summary>
    /// The assumption twin of <c>BoardQueries.ResolveAsync</c>: <c>A1</c> looks anywhere in the
    /// workspace, <c>entity-store/A1</c> inside that run only. More than one row back means the bare
    /// ref is ambiguous, and the caller refuses with the qualified forms rather than resolving both.
    /// </summary>
    private static async Task<IReadOnlyList<OpenAssumption>> OpenAssumptionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        string reference,
        CancellationToken cancellationToken)
    {
        var slash = reference.LastIndexOf('/');
        var qualifier = slash > 0 ? reference[..slash] : null;
        var leaf = slash > 0 ? reference[(slash + 1)..] : reference;

        const string sql =
            """
            SELECT a.id AS "Id",
                   a.ref AS "Ref",
                   CASE WHEN s.ref IS NULL THEN a.ref ELSE s.ref || '/' || a.ref END AS "QualifiedRef"
            FROM   assumption a
            LEFT   JOIN item s ON s.id = a.ref_scope_id
            WHERE  a.workspace_id = @workspace
              AND  a.state = 'open'
              AND  lower(a.ref) = lower(@leaf)
              AND  (@qualifier IS NULL
                    OR lower(s.ref) = lower(@qualifier)
                    OR (a.ref_scope_id IS NULL AND lower(@qualifier) = @everywhere))
            ORDER  BY s.ref
            FOR    NO KEY UPDATE OF a;
            """;

        return (await connection.QueryAsync<OpenAssumption>(new CommandDefinition(
            sql,
            new { workspace = workspaceId, leaf, qualifier, everywhere = Queries.BoardQueries.WorkspaceQualifier },
            transaction,
            cancellationToken: cancellationToken))).AsList();
    }

    /// <summary>Shared by <c>work_assume</c> and by <c>work_report</c>'s <c>assumptions[]</c>.</summary>
    private static async Task<string> AssumeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        string statement,
        EvidenceLevel level,
        ReversalCost cost,
        Guid? revisitItemId,
        CancellationToken cancellationToken,
        string? chosen = null)
    {
        var reference = chosen ?? await FreeAssumptionRefAsync(
            connection, transaction, item.WorkspaceId, item.RefScopeId, cancellationToken);

        const string sql =
            """
            INSERT INTO assumption
                (id, workspace_id, scope_item_id, ref, statement, evidence, reversal, revisit_item_id, session_id)
            VALUES (@id, @workspace, @scope, @ref, @statement,
                    @evidence::evidence_level, @reversal::reversal_cost, @revisit, @session);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = Guid.CreateVersion7(),
                session = actor.SessionId,
                workspace = item.WorkspaceId,
                scope = item.Id,
                @ref = reference,
                statement,
                evidence = Pg.Label(level),
                reversal = Pg.Label(cost),
                revisit = revisitItemId,
            },
            transaction,
            cancellationToken: cancellationToken));

        return reference;
    }

    private static async Task<string> FreeAssumptionRefAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid? refScopeId,
        CancellationToken cancellationToken)
    {
        for (var n = 1; n < 500; n++)
        {
            var candidate = $"A{n}";
            if (!await AssumptionRefTakenAsync(connection, transaction, workspaceId, refScopeId, candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return $"A{Guid.CreateVersion7():n}"[..6];
    }

    private static async Task<bool> AssumptionRefTakenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid? refScopeId,
        string reference,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT EXISTS (
              SELECT 1 FROM assumption
              WHERE workspace_id = @workspace
                AND ref_scope_id IS NOT DISTINCT FROM @scope AND lower(ref) = lower(@ref));
            """;

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql,
            new { workspace = workspaceId, scope = refScopeId, @ref = reference },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static bool Level(string? asked, out EvidenceLevel level)
        => Enum.TryParse(asked?.Replace("_", string.Empty).Trim(), ignoreCase: true, out level)
           && Enum.IsDefined(level);

    private static bool Cost(string? asked, out ReversalCost cost)
        => Enum.TryParse(asked?.Trim(), ignoreCase: true, out cost) && Enum.IsDefined(cost);
}
