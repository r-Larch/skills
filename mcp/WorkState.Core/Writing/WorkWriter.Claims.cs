using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>
/// doc 07 §5. <c>in_progress</c> means someone is working on this <em>right now</em>, and the claim
/// is what makes that falsifiable. Two Postgres guarantees carry it — one live claim per item, one
/// live write claim per <c>serialization_key</c> — so this layer's job is the message and the lease,
/// not the exclusion.
/// </summary>
public sealed partial class WorkWriter
{
    private sealed record Predecessor(string Ref, ItemStatus Status);

    /// <summary>What <c>claim_one_writer_per_tree</c> is keyed on, and the tree the caller named.</summary>
    private sealed record TreeLock(string Key, Guid Workspace, string? Tree);

    private sealed record KeySources(string? RunKey, string? RepoRoot, string? ScopeRef);

    /// <summary>
    /// Refuses a second claim, and says who holds the first. Two runs in different worktrees proceed
    /// concurrently; two sharing a working tree do not — that is the whole parallel-orchestrator
    /// rule, and the refusal names the call that gets on with something else instead.
    /// <para>
    /// Every unreleased claim that <c>claim_one_live_per_item</c>, <c>claim_one_writer_per_tree</c>
    /// or <c>claim_one_session_per_tree</c> would collide with is read here, lapsed-but-unswept ones
    /// too, since a constraint does not know about leases. Two things pass: the holder re-taking its
    /// own item, which extends the lease, and the session that holds a tree claiming another item of
    /// it (doc 11). A claim with no session — one from before doc 11 — still holds its tree alone.
    /// </para>
    /// </summary>
    private async Task<string?> ClaimRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        string? tree,
        CancellationToken cancellationToken)
    {
        var treeLock = await TreeLockAsync(connection, transaction, item, tree, cancellationToken);
        var retake = Retake(item.Ref, treeLock.Tree);

        const string sql =
            """
            SELECT c.item_id           AS "ItemId",
                   c.agent_name        AS "AgentName",
                   c.serialization_key AS "SerializationKey",
                   c.expires_at        AS "ExpiresAt",
                   c.heartbeat_at      AS "HeartbeatAt",
                   i.ref               AS "ItemRef",
                   CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                                       AS "QualifiedRef",
                   c.session_id        AS "SessionId",
                   coalesce(hs.name, c.agent_name) AS "Holder"
            FROM   claim c
            JOIN   item i ON i.id = c.item_id
            LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
            LEFT   JOIN agent_session hs ON hs.id = c.session_id
            WHERE  c.released_at IS NULL
              AND  (c.item_id = @id
                    OR (c.writes AND c.serialization_key = @key
                        AND c.key_workspace_id = @keyWorkspace))
            ORDER  BY (c.item_id = @id) DESC, c.acquired_at;
            """;

        var held = (await connection.QueryAsync<ClaimHolder>(new CommandDefinition(
            sql,
            new { id = item.Id, key = treeLock.Key, keyWorkspace = treeLock.Workspace },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        var live = held.FirstOrDefault(h => h.ItemId == item.Id ? !Retakes(h, actor) : !SameSession(h, actor));
        if (live is null)
        {
            return null;
        }

        var you = live.SessionId is null && actor.SessionId is null && live.AgentName == actor.Name
            ? " — that is you"
            : string.Empty;

        if (live.ItemId == item.Id)
        {
            return Refusal.Blocked(
                $"work_status({item.Ref} → in_progress). {item.Ref} is already claimed.",
                $"{live.Holder} holds a live claim on {item.Ref}; {Lease(live)}",
                "work_next() for something else that is ready",
                $"{retake} once that lease lapses — "
                + "the sweep releases it and returns the item to ready");
        }

        var routes = new List<string>
        {
            $"work_status(ref:\"{live.QualifiedRef}\", to:\"review\") to release it — any move out of "
            + "in_progress (review, blocked, done) releases the claim",
            $"{retake} once that lease lapses — the sweep releases it",
        };

        if (treeLock.Tree is null)
        {
            routes.Add($"work_status(ref:\"{item.Ref}\", to:\"in_progress\", tree:\"<your worktree path>\") "
                       + "if you work in a git worktree of your own — one writer per tree, many per run");
        }

        routes.Add("work_next(scope:\"<another run>\") — two runs in different worktrees proceed in "
                   + "parallel, two sharing one working tree do not");

        return Refusal.Blocked(
            $"work_status({item.Ref} → in_progress). Another writer holds this working tree.",
            $"{live.QualifiedRef} holds the write claim on \"{Glyph.Flat(live.SerializationKey)}\" "
            + $"(held by {live.Holder}{you}); {Lease(live)}",
            [.. routes]);
    }

    /// <summary>The holder re-taking its own item: the same session, or the same name where neither has one.</summary>
    private static bool Retakes(ClaimHolder holder, Actor actor)
        => holder.SessionId is { } session
            ? session == actor.SessionId
            : holder.AgentName == actor.Name;

    /// <summary>
    /// doc 11 — one writer per tree is one session per tree. Only a session can share a tree with
    /// itself; a claim from before sessions holds its tree alone, as it always did.
    /// </summary>
    private static bool SameSession(ClaimHolder holder, Actor actor)
        => holder.SessionId is { } session && session == actor.SessionId;

    private static string Retake(string reference, string? tree)
        => tree is null
            ? $"work_status(ref:\"{reference}\", to:\"in_progress\")"
            : $"work_status(ref:\"{reference}\", to:\"in_progress\", tree:\"{tree}\")";

    private static string Lease(ClaimHolder holder)
    {
        var at = $"{holder.ExpiresAt.UtcDateTime:HH:mm} UTC";
        var left = holder.ExpiresAt - DateTimeOffset.UtcNow;

        return left > TimeSpan.Zero
            ? $"the lease lapses in {Glyph.Age(left)} ({at})"
            : $"the lease lapsed {Glyph.Ago(-left)} ({at}) and is not swept yet";
    }

    private async Task<string> AcquireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        string? tree,
        CancellationToken cancellationToken)
    {
        var ttl = await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken);
        var treeLock = await TreeLockAsync(connection, transaction, item, tree, cancellationToken);

        const string extend =
            """
            UPDATE claim
               SET heartbeat_at = now(), expires_at = now() + make_interval(mins => @ttl),
                   session_id = coalesce(@session, session_id)
             WHERE item_id = @item AND released_at IS NULL
               AND (session_id = @session OR (session_id IS NULL AND agent_name = @agent));
            """;

        var extended = await connection.ExecuteAsync(new CommandDefinition(
            extend,
            new { item = item.Id, agent = actor.Name, session = actor.SessionId, ttl },
            transaction,
            cancellationToken: cancellationToken));

        await SessionTreeAsync(connection, transaction, actor, treeLock.Tree, cancellationToken);

        if (extended > 0)
        {
            return $"extended, {ttl}m left, held by {actor.Label}";
        }

        const string acquire =
            """
            INSERT INTO claim
                (id, item_id, serialization_key, key_workspace_id, tree, writes, agent_name, role,
                 acquired_at, heartbeat_at, expires_at, session_id)
            VALUES (@id, @item, @key, @keyWorkspace, @tree, true, @agent, @role::actor_role,
                    now(), now(), now() + make_interval(mins => @ttl), @session);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            acquire,
            new
            {
                id = Guid.CreateVersion7(),
                item = item.Id,
                key = treeLock.Key,
                keyWorkspace = treeLock.Workspace,
                tree = treeLock.Tree,
                agent = actor.Name,
                role = Pg.Label(actor.Role),
                ttl,
                session = actor.SessionId,
            },
            transaction,
            cancellationToken: cancellationToken));

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "claim_acquired",
            $"{item.Ref} claimed by {actor.Label} for {ttl}m",
            new { agent = actor.Name, session = actor.Session, serialization_key = treeLock.Key, tree = treeLock.Tree, ttl_minutes = ttl },
            cancellationToken);

        return $"acquired by {actor.Label}, {ttl}m lease on \"{Glyph.Flat(treeLock.Key)}\"";
    }

    /// <summary>doc 11 — the tree a session last claimed on, and its first one as its cwd if no header named one.</summary>
    private static async Task SessionTreeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Actor actor,
        string? tree,
        CancellationToken cancellationToken)
    {
        if (actor.SessionId is not { } session || tree is null)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_session SET tree = @tree, cwd = coalesce(cwd, @tree) WHERE id = @session;",
            new { session, tree },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>Leaving <c>in_progress</c> by any route releases the claim — doc 07 §2.</summary>
    private static async Task<string?> ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        ItemStatus status,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE claim SET released_at = now(), release_reason = @reason
             WHERE item_id = @item AND released_at IS NULL;
            """;

        var released = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { item = item.Id, reason = Pg.Label(status) },
            transaction,
            cancellationToken: cancellationToken));

        return released > 0 ? "released" : null;
    }

    /// <summary>
    /// doc 07 §5 — "any write by the claim holder extends it. No separate heartbeat call: a tool
    /// that needs a keep-alive gets forgotten." Every write tool calls this on its way out.
    /// </summary>
    private static async Task HeartbeatAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        Actor actor,
        int ttl,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE claim c
               SET heartbeat_at = now(), expires_at = now() + make_interval(mins => @ttl)
              FROM item i
             WHERE i.id = @item
               AND c.released_at IS NULL
               AND (c.session_id = @session OR (c.session_id IS NULL AND c.agent_name = @agent))
               AND (c.item_id = i.id OR c.item_id = ANY(i.ancestors));
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { item = itemId, agent = actor.Name, session = actor.SessionId, ttl },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<int> TtlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT CASE WHEN settings->>'claim_ttl_minutes' ~ '^[0-9]+$'
                        THEN (settings->>'claim_ttl_minutes')::int
                        ELSE @fallback END
            FROM   workspace WHERE id = @workspace;
            """;

        var ttl = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            sql,
            new { workspace = workspaceId, fallback = DefaultTtlMinutes },
            transaction,
            cancellationToken: cancellationToken));

        return ttl is > 0 ? ttl.Value : DefaultTtlMinutes;
    }

    /// <summary>
    /// The write lock is on the working tree, not the item: the <c>tree</c> the caller named, else
    /// the run's <c>serialization_key</c>, else the project's repo root, else the ref scope. Every key
    /// locks within its workspace — a path as much as a name — so one tenant's claim never holds
    /// another's tree. Walking the ancestor chain server-side keeps <c>uuid[]</c> out of the Dapper
    /// mapping, which needs a handler it does not have.
    /// </summary>
    private static async Task<TreeLock> TreeLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        string? tree,
        CancellationToken cancellationToken)
    {
        if (WorkingTree.Parse(tree) is { } named)
        {
            return new TreeLock(named.Key, item.WorkspaceId, named.Key);
        }

        const string sql =
            """
            SELECT
              (SELECT rc.serialization_key
                 FROM item i
                 CROSS JOIN LATERAL unnest(i.ancestors || i.id) WITH ORDINALITY AS u(id, ord)
                 JOIN run_config rc ON rc.item_id = u.id
                WHERE i.id = @item AND rc.serialization_key <> ''
                ORDER BY u.ord DESC LIMIT 1) AS "RunKey",
              (SELECT pc.repo_root
                 FROM item i
                 CROSS JOIN LATERAL unnest(i.ancestors || i.id) WITH ORDINALITY AS u(id, ord)
                 JOIN project_config pc ON pc.item_id = u.id
                WHERE i.id = @item AND pc.repo_root <> ''
                ORDER BY u.ord DESC LIMIT 1) AS "RepoRoot",
              (SELECT coalesce(s.ref, i.ref)
                 FROM item i LEFT JOIN item s ON s.id = i.ref_scope_id
                WHERE i.id = @item) AS "ScopeRef";
            """;

        var sources = await connection.QuerySingleAsync<KeySources>(new CommandDefinition(
            sql,
            new { item = item.Id },
            transaction,
            cancellationToken: cancellationToken));

        return new TreeLock(sources.RunKey ?? sources.RepoRoot ?? sources.ScopeRef ?? item.Id.ToString(), item.WorkspaceId, null);
    }
}
