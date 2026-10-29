using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>Everything <c>work_constrain</c> takes besides the ref and the retirement.</summary>
public sealed record ConstrainArgs(
    string? Kind = null,
    string? Text = null,
    string? Route = null,
    string? Scope = null,
    string[]? CoversRisk = null,
    string? Severity = null,
    string? From = null,
    bool Retire = false,
    string? Reason = null);

public sealed partial class WorkWriter
{
    /// <summary>
    /// doc 02 §8 — the writer the BINDING block never had. A corpus migrated out of markdown could
    /// carry rules in; a project started in the store could not add one, so its BINDING block was
    /// empty forever.
    /// <para>
    /// Retiring is the other half and the reason this is not a ratchet: the row stays with
    /// <c>active = false</c>, so an audit can still see what used to bind and when it stopped.
    /// </para>
    /// </summary>
    public async Task<WriteResult> ConstrainAsync(
        Guid workspaceId,
        Actor actor,
        string? reference,
        ConstrainArgs args,
        CancellationToken cancellationToken = default)
    {
        if (args.Retire)
        {
            return await RetireAsync(workspaceId, actor, reference, args.Reason, cancellationToken);
        }

        if (Blank(args.Text))
        {
            return WriteResult.No(Refusal.Blocked(
                "work_constrain without text.",
                "the text is the rule — it is what every brief in scope will print",
                "work_constrain(kind:\"red_line\", text:\"…\", route:\"…\")",
                "work_constrain(ref:\"C4\", retire:true, reason:\"…\") to retract one instead"));
        }

        if (!Kind(args.Kind, out var kind))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_constrain(kind:\"{args.Kind}\") — \"{args.Kind}\" is not a constraint kind.",
                "the kind decides how it renders: a red_line prints as ! and everything else as ·",
                "work_constrain(kind:\"red_line\", text:\"…\", route:\"…\") — also stack_fact, "
                + "command, domain_rule, generated_code"));
        }

        // The hardest-won rule in the corpus, made a guard rather than a comment: a prohibition with
        // no way out is a trap the author set themselves, and it is satisfiable in every case —
        // "stop and report it" is a route. Required only for red_line: a stack_fact or a command is
        // not a prohibition, and demanding a route there would be the unsatisfiable guard of doc 07
        // §3 that teaches everyone the override.
        if (kind == ConstraintKind.RedLine && Blank(args.Route))
        {
            return WriteResult.No(Refusal.Blocked(
                "work_constrain(kind:\"red_line\") without a route.",
                "a prohibition with no way out is a trap the author set themselves — the agent that "
                + "hits it at 3am has nothing to do but guess or stop",
                $"work_constrain(kind:\"red_line\", text:\"{Glyph.Flat(args.Text)}\", "
                + "route:\"…what to do instead, or who to ask\")",
                "work_constrain(kind:\"domain_rule\", text:\"…\") if it is not actually a prohibition"));
        }

        Guid? scopeItemId = null;
        string scopeRef = "the workspace";

        if (!Blank(args.Scope))
        {
            var (scope, unresolved) = await ResolveAsync(workspaceId, args.Scope, cancellationToken);
            if (scope is null)
            {
                return WriteResult.No(unresolved!);
            }

            scopeItemId = scope.Id;
            scopeRef = scope.Ref;
        }

        Guid? addedBy = null;
        if (!Blank(args.From))
        {
            var (source, unresolved) = await ResolveAsync(workspaceId, args.From, cancellationToken);
            if (source is null)
            {
                return WriteResult.No(unresolved!);
            }

            addedBy = source.Id;
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var (_, minted) = await InsertConstraintAsync(
            connection, transaction, workspaceId, scopeItemId, kind, args, addedBy, cancellationToken);

        await EventAsync(
            connection,
            transaction,
            workspaceId,
            scopeItemId ?? addedBy,
            actor,
            "constraint",
            $"{minted} ({Pg.Label(kind)}) binds {scopeRef}: {Glyph.Flat(args.Text)}",
            new { @ref = minted, kind = Pg.Label(kind), scope = args.Scope, text = args.Text, route = args.Route },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var lines = new List<string>
        {
            $"OK  {minted}  {Pg.Label(kind)} recorded, binding on {scopeRef}",
            $"    {(kind == ConstraintKind.RedLine ? "!" : "·")} {Glyph.Row(args.Text, 92)}",
        };

        if (!Blank(args.Route))
        {
            lines.Add($"    → {Glyph.Flat(args.Route)}");
        }

        lines.Add(string.Empty);
        lines.Add($"Next: nothing — it prints as {minted} in the BINDING block of every brief in "
                + $"scope, until work_constrain(ref:\"{minted}\", retire:true, reason:\"…\").");

        return WriteResult.Ok(string.Join("\n", lines));
    }

    /// <summary>Shared by <c>work_constrain</c> and <c>work_import</c>'s rule records.</summary>
    private static async Task<(Guid Id, string Ref)> InsertConstraintAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid? scopeItemId,
        ConstraintKind kind,
        ConstrainArgs args,
        Guid? addedBy,
        CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var minted = await FreeConstraintRefAsync(connection, transaction, workspaceId, cancellationToken);

        const string sql =
            """
            INSERT INTO constraint_rule
                (id, workspace_id, scope_item_id, ref, kind, text, route, covers_risk, severity,
                 added_by_item_id)
            VALUES (@id, @workspace, @scope, @ref, @kind::constraint_kind, @text, @route,
                    @covers, @severity, @from);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id,
                workspace = workspaceId,
                scope = scopeItemId,
                @ref = minted,
                kind = Pg.Label(kind),
                text = args.Text!.Trim(),
                route = Blank(args.Route) ? null : args.Route!.Trim(),
                covers = args.CoversRisk ?? [],
                severity = Blank(args.Severity) ? "binding" : args.Severity!.Trim().ToLowerInvariant(),
                from = addedBy,
            },
            transaction,
            cancellationToken: cancellationToken));

        return (id, minted);
    }

    private async Task<WriteResult> RetireAsync(
        Guid workspaceId,
        Actor actor,
        string? reference,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (Blank(reference))
        {
            return WriteResult.No(Refusal.Blocked(
                "work_constrain(retire:true) without a ref.",
                "retiring needs to know which rule, and the ref is the only address there is",
                "work_brief() prints the ref of every rule that binds you",
                $"read {WorkUris.Conventions} for the ones the brief clipped"));
        }

        // Retracting is terminal for everything downstream of it — the next brief simply stops
        // printing the rule — so the reason is the whole record of why it no longer binds.
        if (Blank(reason))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_constrain(ref:\"{reference}\", retire:true) without a reason.",
                "the row stays for the audit; without a reason it records only that the rule "
                + "stopped binding, never why",
                $"work_constrain(ref:\"{reference}\", retire:true, reason:\"…\")"));
        }

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        const string sql =
            """
            UPDATE constraint_rule
               SET active = false
             WHERE workspace_id = @workspace
               AND lower(ref) = lower(@ref)
               AND active
            RETURNING ref, text;
            """;

        var row = await connection.QuerySingleOrDefaultAsync<RetiredRule>(new CommandDefinition(
            sql,
            new { workspace = workspaceId, @ref = reference!.Trim() },
            transaction,
            cancellationToken: cancellationToken));

        if (row is null)
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_constrain(ref:\"{reference}\", retire:true) — no active rule has that ref.",
                "it is already retired, or the ref is from another workspace",
                "work_brief() prints every rule that binds you, with its ref",
                $"read {WorkUris.Conventions} for every rule active in scope"));
        }

        await EventAsync(
            connection,
            transaction,
            workspaceId,
            null,
            actor,
            "constraint",
            $"{row.Ref} retired: {Glyph.Flat(reason)}",
            new { @ref = row.Ref, retired = true, reason },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return WriteResult.Ok(string.Join(
            "\n",
            $"OK  {row.Ref}  retired; the row stays with active = false, because the record is the point",
            $"    {Glyph.Flat(row.Text)}",
            $"    reason  {Glyph.Row(reason, 86)}",
            string.Empty,
            "Next: nothing — the next brief in scope no longer prints it."));
    }

    /// <summary>
    /// The first free <c>C…</c> in the workspace, by the probe <see cref="FreeAssumptionRefAsync"/>
    /// uses for <c>A…</c>. The scope is the workspace and not the run: one BINDING block renders the
    /// workspace-wide rules together with the ones scoped to the item's ancestors, so a per-run
    /// number would print two <c>C1</c>s side by side.
    /// </summary>
    private static async Task<string> FreeConstraintRefAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT EXISTS (
              SELECT 1 FROM constraint_rule
              WHERE workspace_id = @workspace AND lower(ref) = lower(@ref));
            """;

        for (var n = 1; n < 500; n++)
        {
            var candidate = $"C{n}";
            var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                sql,
                new { workspace = workspaceId, @ref = candidate },
                transaction,
                cancellationToken: cancellationToken));

            if (!taken)
            {
                return candidate;
            }
        }

        return $"C{Guid.CreateVersion7():n}"[..6];
    }

    private static bool Kind(string? asked, out ConstraintKind kind)
        => Enum.TryParse(asked?.Replace("_", string.Empty).Trim(), ignoreCase: true, out kind)
           && Enum.IsDefined(kind);

    private sealed record RetiredRule(string Ref, string Text);
}
