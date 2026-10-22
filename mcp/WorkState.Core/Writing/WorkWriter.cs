using System.Text.Json;
using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>
/// One write and what the caller should read next. <see cref="Refused"/> is carried rather than
/// thrown because a refusal is the product (doc 06 §6): it is a value with a route, exactly as
/// <c>ScopeResult</c> makes a missing scope a value rather than an exception.
/// </summary>
public sealed record WriteResult(string Text, bool Refused)
{
    public static WriteResult Ok(string text) => new(text, false);

    public static WriteResult No(string text) => new(text, true);
}

/// <summary>Everything <c>work_status</c> takes besides the ref and the move.</summary>
public sealed record StatusArgs(
    string? Reason = null,
    string? Target = null,
    string? Resolution = null,
    string? Commit = null,
    bool Force = false);

/// <summary>
/// The write side. It lives in <c>WorkState.Core</c> beside the renderers for the same reason they
/// do: the guards are domain logic and the MCP tools are a thin transport over them, so every rule
/// here is testable against a real database without a web host.
/// <para>
/// What Postgres already refuses is <b>not</b> re-implemented (doc 07 §1): duplicate refs, blocks
/// cycles, resolution without done, two live write-claims on one tree and an edited event are
/// constraints, and this layer catches the <see cref="PostgresException"/> and surfaces its message.
/// </para>
/// </summary>
public sealed partial class WorkWriter(NpgsqlDataSource dataSource, BoardQueries board, Housekeeping housekeeping)
{
    /// <summary>doc 07 §5 — 30 minutes unless <c>workspace.settings</c> says otherwise.</summary>
    private const int DefaultTtlMinutes = 30;

    private sealed record ItemRow(
        Guid Id,
        Guid WorkspaceId,
        string Ref,
        string Kind,
        string Title,
        string? Intent,
        ItemStatus Status,
        string? VerifyCmd,
        Guid? ParentId,
        Guid? RefScopeId,
        int Revision,
        bool RequiresVerify,
        bool RequiresCommit,
        bool RequiresCriteria);

    private sealed record ClaimHolder(
        Guid ItemId,
        string AgentName,
        string SerializationKey,
        DateTimeOffset ExpiresAt,
        DateTimeOffset HeartbeatAt,
        string ItemRef,
        string QualifiedRef,
        bool SameWorkspace);

    /// <summary>
    /// doc 07 §2 and §3. The transition table decides what is reachable, the five conditions decide
    /// whether <c>done</c> is earned, and <c>force</c> is named in the refusal rather than hidden
    /// behind it — an escape hatch that is hidden gets worked around, one that leaves a mark gets
    /// used honestly.
    /// </summary>
    public async Task<WriteResult> StatusAsync(
        Guid workspaceId,
        string? reference,
        string? to,
        Actor actor,
        StatusArgs args,
        CancellationToken cancellationToken = default)
    {
        await housekeeping.SweepAsync(workspaceId, cancellationToken);

        if (!Transitions.TryParse(to, out var move))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_status({reference ?? "?"} → \"{to}\") — \"{to}\" is not a move.",
                "to: takes one of the moves doc 07 §2 draws",
                $"work_status(ref:\"{reference}\", to:\"in_progress\") — also ready, blocked, "
                + "review, done, cancelled, deferred"));
        }

        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);

        try
        {
            return await TransitionAsync(connection, transaction, item, move, actor, args, cancellationToken);
        }
        catch (PostgresException refused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WriteResult.No(
                await SchemaRefusalAsync(connection, item, to!, actor, refused, cancellationToken));
        }
    }

    /// <summary>
    /// The guards read before anything is written, but a concurrent writer can still take the slot
    /// between the read and the insert — and then the index says no. That answer is re-read as the
    /// same refusal the guard would have given, holder and all, because a schema message naming an
    /// index is a problem without a route (doc 04 §3).
    /// </summary>
    private async Task<string> SchemaRefusalAsync(
        NpgsqlConnection connection,
        ItemRow item,
        string to,
        Actor actor,
        PostgresException refused,
        CancellationToken cancellationToken)
    {
        if (refused.ConstraintName is "claim_one_live_per_item" or "claim_one_writer_per_tree")
        {
            await using var reread = await connection.BeginTransactionAsync(cancellationToken);
            var held = await ClaimRefusalAsync(connection, reread, item, actor, cancellationToken);
            if (held is not null)
            {
                return held;
            }
        }

        return FromPostgres(refused, $"work_status(ref:\"{item.Ref}\", to:\"{to.Trim()}\")");
    }

    private async Task<WriteResult> TransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Move move,
        Actor actor,
        StatusArgs args,
        CancellationToken cancellationToken)
    {
        if (!Transitions.Legal(item.Status, move, item.Kind))
        {
            return WriteResult.No(Transitions.Refuse(item.Ref, item.Status, move, item.Kind));
        }

        if (move == Move.Deferred)
        {
            return await DeferAsync(connection, transaction, item, actor, args, cancellationToken);
        }

        var refusal = await GuardAsync(connection, transaction, item, move, actor, args, cancellationToken);
        if (refusal is not null)
        {
            return WriteResult.No(refusal);
        }

        var forced = await ForcedAsync(connection, transaction, item, move, args, cancellationToken);
        var status = StatusOf(move);
        var resolution = ResolutionFor(status, args.Resolution);

        if (args.Commit is { Length: > 0 } sha)
        {
            await ArtifactAsync(connection, transaction, item.Id, "commit", sha, cancellationToken);
        }

        await ApplyAsync(connection, transaction, item.Id, status, resolution, actor, cancellationToken);

        var claimLine = status == ItemStatus.InProgress
            ? await AcquireAsync(connection, transaction, item, actor, cancellationToken)
            : await ReleaseAsync(connection, transaction, item, status, cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            forced is null ? "status" : "forced_transition",
            $"{item.Ref} {Transitions.Name(item.Status)} → {Transitions.Name(status)}"
            + (args.Reason is { Length: > 0 } ? $": {Glyph.Clip(args.Reason, 40)}" : string.Empty),
            new
            {
                from = Transitions.Name(item.Status),
                to = Transitions.Name(status),
                reason = args.Reason,
                forced,
            },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return WriteResult.Ok(Wrote(item, status, args, forced, claimLine));
    }

    private static string Wrote(
        ItemRow item,
        ItemStatus status,
        StatusArgs args,
        string[]? forced,
        string? claimLine)
    {
        var lines = new List<string>
        {
            $"OK  {item.Ref}  {Transitions.Name(item.Status)} → {Transitions.Name(status)}",
        };

        if (args.Reason is { Length: > 0 })
        {
            lines.Add($"    reason  {Glyph.Clip(args.Reason, 86)}");
        }

        if (args.Commit is { Length: > 0 })
        {
            lines.Add($"    commit  {Glyph.Clip(args.Commit, 86)}");
        }

        if (claimLine is { Length: > 0 })
        {
            lines.Add($"    claim   {claimLine}");
        }

        if (forced is { Length: > 0 })
        {
            lines.Add($"    FORCED over {forced.Length} unmet condition(s) — recorded as a "
                    + "forced_transition, visible in");
            lines.Add("            work_brief and work_audit until someone closes it honestly:");
            lines.AddRange(forced.Select(f => $"              · {Glyph.Clip(f, 84)}"));
        }

        lines.Add(string.Empty);
        lines.Add(status switch
        {
            ItemStatus.InProgress => $"Next: do the work, then work_verify(ref:\"{item.Ref}\", "
                                     + "command:\"…\", result_line:\"…\", run_by:\"orchestrator\").",
            ItemStatus.Done => "Next: work_next() — the frontier has just moved.",
            ItemStatus.Review => $"Next: work_verify(ref:\"{item.Ref}\", …) then "
                                 + $"work_status(ref:\"{item.Ref}\", to:\"done\").",
            ItemStatus.Blocked => $"Next: work_threads(scope:\"{item.Ref}\", include:\"deps\") — "
                                  + "what this is waiting on.",
            _ => "Next: work_next() for what is ready now.",
        });

        return string.Join("\n", lines);
    }

    /// <summary>
    /// doc 07 §2's per-row guards, plus §3's five. Everything that can refuse does so here, before
    /// anything is written, and each returns the whole refusal rather than the first line of it.
    /// </summary>
    private async Task<string?> GuardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Move move,
        Actor actor,
        StatusArgs args,
        CancellationToken cancellationToken)
    {
        switch (move)
        {
            case Move.Ready:
                return await ReadyRefusalAsync(connection, transaction, item, cancellationToken);

            case Move.InProgress:
                return await ClaimRefusalAsync(connection, transaction, item, actor, cancellationToken);

            case Move.Blocked when string.IsNullOrWhiteSpace(args.Reason):
                return Refusal.Blocked(
                    $"work_status({item.Ref} → blocked) without a reason.",
                    "a block nobody can read is the 84.5% failure in miniature — the next agent has "
                    + "to rediscover what stopped this",
                    $"work_status(ref:\"{item.Ref}\", to:\"blocked\", reason:\"…\")");

            case Move.Cancelled when string.IsNullOrWhiteSpace(args.Reason):
                return Refusal.Blocked(
                    $"work_status({item.Ref} → cancelled) without a reason.",
                    "cancelling is terminal and cannot be undone — the reason is the whole record of it",
                    $"work_status(ref:\"{item.Ref}\", to:\"cancelled\", reason:\"…\")",
                    $"work_status(ref:\"{item.Ref}\", to:\"deferred\", target:\"backlog\", reason:\"…\") "
                    + "if the work still needs doing, just not here");

            case Move.Done:
                return await DoneRefusalAsync(connection, transaction, item, args, cancellationToken);

            default:
                return null;
        }
    }

    private async Task<string?> DoneRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        StatusArgs args,
        CancellationToken cancellationToken)
    {
        var unmet = DoneGuard.Evaluate(
            await DoneFactsAsync(connection, transaction, item, args.Commit, cancellationToken));

        if (unmet.Count == 0)
        {
            return null;
        }

        if (!args.Force)
        {
            return Refusal.Transition(item.Ref, "done", unmet);
        }

        return string.IsNullOrWhiteSpace(args.Reason)
            ? Refusal.Blocked(
                $"work_status({item.Ref} → done, force:true) without a reason.",
                $"force writes a forced_transition over {unmet.Count} unmet condition(s) and it stays "
                + "visible in work_brief and work_audit — the reason is what will be read there",
                $"work_status(ref:\"{item.Ref}\", to:\"done\", force:true, reason:\"…\")")
            : null;
    }

    /// <summary>The unmet conditions a <c>force</c> went over, so the event records what was skipped.</summary>
    private async Task<string[]?> ForcedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Move move,
        StatusArgs args,
        CancellationToken cancellationToken)
    {
        if (move != Move.Done || !args.Force)
        {
            return null;
        }

        var unmet = DoneGuard.Evaluate(
            await DoneFactsAsync(connection, transaction, item, args.Commit, cancellationToken));

        return unmet.Count == 0 ? null : [.. unmet.Select(u => u.Problem)];
    }

    private async Task<string?> ReadyRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT b.ref AS "Ref", b.status AS "Status"
            FROM   item_link l
            JOIN   item b ON b.id = l.from_item_id
            WHERE  l.to_item_id = @id
              AND  l.type = 'blocks'
              AND  b.status NOT IN ('done','cancelled','superseded')
            ORDER  BY b.ref;
            """;

        var open = (await connection.QueryAsync<Predecessor>(
            new CommandDefinition(sql, new { id = item.Id }, transaction, cancellationToken: cancellationToken)))
            .AsList();

        if (open.Count == 0)
        {
            return null;
        }

        return Refusal.Blocked(
            $"work_status({item.Ref} → ready). {open.Count} blocking predecessor(s) unfinished.",
            [
                .. open.Take(4).Select(b => new Unmet(
                    $"{b.Ref} blocks {item.Ref} and is {Transitions.Name(b.Status)}",
                    [
                        $"work_status(ref:\"{b.Ref}\", to:\"done\")",
                        $"work_link(from:\"{b.Ref}\", to:\"{item.Ref}\", type:\"blocks\", remove:true) "
                        + "if that dependency is stale",
                    ])),
            ]);
    }

    private static ItemStatus StatusOf(Move move) => move switch
    {
        Move.Ready => ItemStatus.Ready,
        Move.InProgress => ItemStatus.InProgress,
        Move.Blocked => ItemStatus.Blocked,
        Move.Review => ItemStatus.Review,
        Move.Done => ItemStatus.Done,
        Move.Cancelled => ItemStatus.Cancelled,
        Move.Superseded => ItemStatus.Superseded,
        _ => ItemStatus.Open,
    };

    /// <summary>
    /// <c>item_resolution_only_when_done</c> is a check constraint: <c>done</c> demands a resolution
    /// and every other status forbids one. A default here is what keeps that from being an error the
    /// agent has to learn.
    /// </summary>
    private static ItemResolution? ResolutionFor(ItemStatus status, string? asked)
    {
        if (status != ItemStatus.Done)
        {
            return null;
        }

        return Enum.TryParse<ItemResolution>(asked?.Trim(), ignoreCase: true, out var parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : ItemResolution.Accepted;
    }

    private static async Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        ItemStatus status,
        ItemResolution? resolution,
        Actor actor,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE item
               SET status     = @status::item_status,
                   resolution = @resolution::item_resolution,
                   started_at = CASE WHEN @status = 'in_progress'
                                     THEN coalesce(started_at, now()) ELSE started_at END,
                   closed_at  = CASE WHEN @status IN ('done','cancelled','superseded')
                                     THEN coalesce(closed_at, now()) ELSE NULL END,
                   closed_by  = CASE WHEN @status IN ('done','cancelled','superseded')
                                     THEN @actor ELSE closed_by END,
                   revision   = revision + 1
             WHERE id = @id;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id,
                status = Pg.Label(status),
                resolution = resolution is null ? null : Pg.Label(resolution.Value),
                actor = actor.Name,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ItemRow> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT i.id           AS "Id",
                   i.workspace_id AS "WorkspaceId",
                   i.ref          AS "Ref",
                   i.kind         AS "Kind",
                   i.title        AS "Title",
                   i.intent       AS "Intent",
                   i.status       AS "Status",
                   i.verify_cmd   AS "VerifyCmd",
                   i.parent_id    AS "ParentId",
                   i.ref_scope_id AS "RefScopeId",
                   i.revision     AS "Revision",
                   k.requires_verify   AS "RequiresVerify",
                   k.requires_commit   AS "RequiresCommit",
                   k.requires_criteria AS "RequiresCriteria"
            FROM   item i
            JOIN   item_kind k ON k.key = i.kind
            WHERE  i.id = @id
            FOR    NO KEY UPDATE OF i;
            """;

        return await connection.QuerySingleAsync<ItemRow>(
            new CommandDefinition(sql, new { id }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<DoneFacts> DoneFactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        string? commit,
        CancellationToken cancellationToken)
    {
        const string criteriaSql =
            """
            SELECT seq AS "Seq", text AS "Text", checked AS "Checked"
            FROM   item_criterion WHERE item_id = @id ORDER BY seq;
            """;

        const string verificationSql =
            """
            SELECT passed AS "Passed", command AS "Command", run_by AS "RunBy"
            FROM   verification WHERE item_id = @id ORDER BY at DESC;
            """;

        // A finding is a child row, but condition 4 governs it and only at blocker severity — so a
        // minor finding must not close this off through condition 3's back door. A guard that
        // refuses honest work as well as dishonest work is the failure this unit is most at risk of.
        const string childrenSql =
            """
            SELECT ref FROM item
            WHERE  parent_id = @id
              AND  kind <> 'finding'
              AND  status NOT IN ('done','cancelled','superseded')
            ORDER  BY seq;
            """;

        const string blockerSql =
            """
            SELECT ref AS "Ref", title AS "Title"
            FROM   item
            WHERE  parent_id = @id
              AND  kind = 'finding'
              AND  severity = 'blocker'
              AND  status NOT IN ('done','cancelled','superseded')
            ORDER  BY ref;
            """;

        const string commitSql =
            "SELECT EXISTS (SELECT 1 FROM artifact WHERE item_id = @id AND kind = 'commit');";

        var parameters = new { id = item.Id };

        var criteria = (await connection.QueryAsync<CriterionFact>(
            new CommandDefinition(criteriaSql, parameters, transaction, cancellationToken: cancellationToken)))
            .AsList();

        var verifications = (await connection.QueryAsync<VerificationFact>(
            new CommandDefinition(verificationSql, parameters, transaction, cancellationToken: cancellationToken)))
            .AsList();

        var children = (await connection.QueryAsync<string>(
            new CommandDefinition(childrenSql, parameters, transaction, cancellationToken: cancellationToken)))
            .AsList();

        var blockers = (await connection.QueryAsync<BlockerFact>(
            new CommandDefinition(blockerSql, parameters, transaction, cancellationToken: cancellationToken)))
            .AsList();

        var hasCommit = commit is { Length: > 0 } || await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(commitSql, parameters, transaction, cancellationToken: cancellationToken));

        return new DoneFacts(
            item.Ref,
            item.Kind,
            item.VerifyCmd,
            item.RequiresVerify,
            item.RequiresCommit,
            hasCommit,
            criteria,
            verifications,
            children,
            blockers);
    }

    private async Task<(ScopeRef? Item, string? Refusal)> ResolveAsync(
        Guid workspaceId,
        string? reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, Refusal.Blocked(
                "this call needs a ref.",
                "every write addresses one item, and a ref is the only address there is",
                "work_next() for what is ready, or work_tree() for every ref that exists"));
        }

        var matches = await board.ResolveAsync(workspaceId, reference.Trim(), cancellationToken);

        return matches.Count switch
        {
            1 => (matches[0], null),
            > 1 => (null, Refusal.Blocked(
                $"ref \"{reference}\" matches {matches.Count} items across runs.",
                "a bare ref is scoped to one run; this one exists in more than one",
                [.. matches.Take(4).Select(m => $"retry with ref:\"{m.QualifiedRef ?? m.Ref}\"")])),
            _ => (null, Refusal.Blocked(
                $"no item has ref \"{reference}\".",
                "refs come from the server, never from invention",
                $"work_search(q:\"{reference}\") to find it",
                "work_tree() to see every ref that exists")),
        };
    }

    private static async Task EventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid? itemId,
        Actor actor,
        string kind,
        string summary,
        object payload,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary)
            VALUES (@workspace, @item, @actor, @role::actor_role, @kind, @payload::jsonb, @summary);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                workspace = workspaceId,
                item = itemId,
                actor = actor.Name,
                role = Pg.Label(actor.Role),
                kind,
                payload = JsonSerializer.Serialize(payload),
                summary = Glyph.Clip(summary, 120),
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task ArtifactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        string kind,
        string value,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO artifact (id, item_id, kind, value)
            VALUES (@id, @item, @kind, @value);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { id = Guid.CreateVersion7(), item = itemId, kind, value },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>The first ref of the given shape that is free inside this item's ref scope.</summary>
    private static async Task<string> FreeRefAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? refScopeId,
        Func<int, string> shape,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT EXISTS (
              SELECT 1 FROM item
              WHERE ref_scope_id IS NOT DISTINCT FROM @scope AND lower(ref) = lower(@ref));
            """;

        for (var n = 1; n < 500; n++)
        {
            var candidate = shape(n);
            var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                sql,
                new { scope = refScopeId, @ref = candidate },
                transaction,
                cancellationToken: cancellationToken));

            if (!taken)
            {
                return candidate;
            }
        }

        return $"x{Guid.CreateVersion7():n}"[..8];
    }

    private static async Task<decimal> NextSeqAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid? parentId,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT coalesce(max(seq), 0) + 1 FROM item WHERE parent_id IS NOT DISTINCT FROM @parent;";

        return await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            sql,
            new { parent = parentId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
