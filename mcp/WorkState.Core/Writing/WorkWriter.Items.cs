using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

public sealed partial class WorkWriter
{
    private sealed record NewRow(
        Guid Id,
        Guid WorkspaceId,
        Guid? ParentId,
        string Kind,
        string Ref,
        decimal Seq,
        string Title,
        string? Intent = null,
        string? VerifyCmd = null,
        string[]? RiskTags = null,
        short Priority = 0,
        string? Severity = null,
        string? Source = null);

    /// <summary>
    /// doc 07 §4. There is no <c>deferred</c> status, on purpose: <c>deferred</c> as a state is
    /// where work goes to disappear, and the corpus has 151 mentions of it behind a follow-up list
    /// nobody ever closed. The move creates a real open item somewhere else, links it back, and
    /// closes this one as <c>obsolete</c> — sound, because the work did not vanish, it moved.
    /// </summary>
    private async Task<WriteResult> DeferAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        StatusArgs args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(args.Target))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_status({item.Ref} → deferred) without a target.",
                "deferring to nowhere is the failure this whole mechanism exists to prevent, so it "
                + "is not expressible — target: says where the work actually goes",
                $"work_status(ref:\"{item.Ref}\", to:\"deferred\", target:\"backlog\", reason:\"…\")",
                $"work_status(ref:\"{item.Ref}\", to:\"deferred\", target:\"<a phase or run ref>\", "
                + "reason:\"…\")"));
        }

        if (string.IsNullOrWhiteSpace(args.Reason))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_status({item.Ref} → deferred) without a reason.",
                "the reason is what tells whoever picks the new item up why it was not done here",
                $"work_status(ref:\"{item.Ref}\", to:\"deferred\", target:\"{args.Target}\", "
                + "reason:\"…\")"));
        }

        var parent = await TargetAsync(connection, transaction, item, args.Target, cancellationToken);
        if (parent is null)
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_status({item.Ref} → deferred, target:\"{args.Target}\") — no item has that ref.",
                "target: is a ref this server knows, or the literal \"backlog\"",
                $"work_status(ref:\"{item.Ref}\", to:\"deferred\", target:\"backlog\", reason:\"…\")",
                "work_tree() to see every ref that exists"));
        }

        var carried = new NewRow(
            Guid.CreateVersion7(),
            item.WorkspaceId,
            parent.Id,
            "followup",
            await FreeRefAsync(
                connection,
                transaction,
                await ScopeUnderAsync(connection, transaction, parent.Id, "followup", cancellationToken),
                n => n == 1 ? $"{item.Ref}-d" : $"{item.Ref}-d{n}",
                cancellationToken),
            await NextSeqAsync(connection, transaction, parent.Id, cancellationToken),
            Glyph.Clip(item.Title, 180),
            item.Intent,
            item.VerifyCmd);

        await InsertItemAsync(connection, transaction, carried, cancellationToken);
        await CopyCriteriaAsync(connection, transaction, item.Id, carried.Id, cancellationToken);
        await LinkAsync(connection, transaction, item.WorkspaceId, carried.Id, item.Id,
            LinkType.DerivedFrom, actor, cancellationToken);

        await ApplyAsync(
            connection, transaction, item.Id, ItemStatus.Done, ItemResolution.Obsolete, actor, cancellationToken);
        await ReleaseAsync(connection, transaction, item, ItemStatus.Done, cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "deferred",
            $"{item.Ref} deferred to {carried.Ref} under {parent.Ref}: {Glyph.Clip(args.Reason, 40)}",
            new { moved_to = carried.Ref, under = parent.Ref, target = args.Target, reason = args.Reason },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var scope = await ScopeFormAsync(item.WorkspaceId, parent, cancellationToken);

        return WriteResult.Ok(string.Join(
            "\n",
            $"OK  {item.Ref}  {Transitions.Name(item.Status)} → done (obsolete) — deferred, not dropped",
            $"    moved to  {carried.Ref} (followup, open) under {parent.Ref}, carrying its criteria",
            $"    link      {carried.Ref} derived_from {item.Ref}",
            $"    reason    {Glyph.Clip(args.Reason, 84)}",
            string.Empty,
            $"Next: work_next(scope:\"{scope}\") — {carried.Ref} is open and on the frontier there."));
    }

    /// <summary>
    /// A ref that resolves to <paramref name="holder"/> alone: the bare ref, else its qualified
    /// form, else run-qualified-by-itself — a project backlog run named "backlog" shares its bare ref
    /// with any in-run backlog phase left from before ws/12.
    /// </summary>
    private async Task<string> ScopeFormAsync(Guid workspaceId, Carried holder, CancellationToken cancellationToken)
    {
        var matches = await board.ResolveAsync(workspaceId, holder.Ref, cancellationToken);
        if (matches is [var only] && only.Id == holder.Id)
        {
            return holder.Ref;
        }

        var self = matches.FirstOrDefault(m => m.Id == holder.Id);
        return self?.QualifiedRef is { } qualified && qualified != holder.Ref
            ? qualified
            : $"{holder.Ref}/{holder.Ref}";
    }

    /// <summary>
    /// Resolves <c>target:</c>. The literal <c>"backlog"</c> is the project-level backlog run,
    /// materialised on first use, because every route this server prints has to be callable
    /// (doc 04 §3) and the refusal for a blocker finding prints exactly that word. It is never an
    /// in-run phase: ws/12 found that one holds the run open, which is deferring to nowhere again.
    /// </summary>
    private static async Task<Carried?> TargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        string target,
        CancellationToken cancellationToken)
    {
        if (target.Trim().Equals("backlog", StringComparison.OrdinalIgnoreCase))
        {
            return await BacklogRunAsync(connection, transaction, item.WorkspaceId, item.Id, cancellationToken);
        }

        const string byRef =
            """
            SELECT id AS "Id", ref AS "Ref" FROM item
            WHERE  workspace_id = @workspace AND lower(ref) = lower(@ref)
              AND  status NOT IN ('cancelled','superseded')
            ORDER  BY (ref_scope_id = @scope) DESC, seq
            LIMIT  1;
            """;

        return await connection.QueryFirstOrDefaultAsync<Carried>(new CommandDefinition(
            byRef,
            new { workspace = item.WorkspaceId, @ref = target.Trim(), scope = item.RefScopeId },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The ref scope a new <paramref name="kind"/> under <paramref name="parentId"/> will land in,
    /// computed by the same function the tree trigger uses — a deferral target in another run puts
    /// the follow-up in that run's numbering, not the source's.
    /// </summary>
    private static async Task<Guid?> ScopeUnderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid parentId,
        string kind,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT item_scope_of(@kind, ancestors || id, NULL) FROM item WHERE id = @parent;";

        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            sql,
            new { kind, parent = parentId },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Raw rather than Dapper because of <c>risk_tags</c>: Dapper expands an <c>IEnumerable</c>
    /// parameter into an <c>IN</c> list, which silently mangles a <c>text[]</c> column.
    /// </summary>
    private static async Task InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NewRow row,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO item (id, workspace_id, parent_id, kind, ref, seq, title, intent,
                              verify_cmd, risk_tags, priority, severity, source)
            VALUES (@id, @workspace, @parent, @kind, @ref, @seq, @title, @intent,
                    @verify, @risk, @priority, @severity, @source);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        Bind(command, "id", row.Id);
        Bind(command, "workspace", row.WorkspaceId);
        Bind(command, "parent", row.ParentId);
        Bind(command, "kind", row.Kind);
        Bind(command, "ref", row.Ref);
        Bind(command, "seq", row.Seq);
        Bind(command, "title", row.Title);
        Bind(command, "intent", row.Intent);
        Bind(command, "verify", row.VerifyCmd);
        Bind(command, "risk", row.RiskTags ?? []);
        Bind(command, "priority", row.Priority);
        Bind(command, "severity", row.Severity);
        Bind(command, "source", row.Source);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Bind(NpgsqlCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static async Task CopyCriteriaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid fromItemId,
        Guid toItemId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO item_criterion (id, item_id, seq, text, checked)
            SELECT gen_random_uuid(), @to, seq, text, false
            FROM   item_criterion WHERE item_id = @from
            ORDER  BY seq;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { from = fromItemId, to = toItemId },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The cycle check and the <c>supersedes</c> side effect are U1 triggers, so this writes the row
    /// and lets Postgres refuse it — doc 07 §1.
    /// </summary>
    private static async Task LinkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid fromItemId,
        Guid toItemId,
        LinkType type,
        Actor actor,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO item_link (id, workspace_id, from_item_id, to_item_id, type, created_by)
            VALUES (@id, @workspace, @from, @to, @type::link_type, @actor)
            ON CONFLICT (from_item_id, to_item_id, type) DO NOTHING;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = Guid.CreateVersion7(),
                workspace = workspaceId,
                from = fromItemId,
                to = toItemId,
                type = Pg.Label(type),
                actor = actor.Name,
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
