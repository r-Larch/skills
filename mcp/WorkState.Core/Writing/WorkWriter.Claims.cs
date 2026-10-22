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

    /// <summary>
    /// Refuses a second claim, and says who holds the first. Two runs in different worktrees proceed
    /// concurrently; two sharing a working tree do not — that is the whole parallel-orchestrator
    /// rule, and the refusal names the call that gets on with something else instead.
    /// <para>
    /// Every unreleased claim that <c>claim_one_live_per_item</c> or <c>claim_one_writer_per_tree</c>
    /// would collide with is read here — the holder's own name included, since one agent holding
    /// two items of one tree is exactly what the index refuses, and lapsed-but-unswept ones too,
    /// since the index does not know about leases. Only the holder re-taking its own item passes:
    /// that extends the lease.
    /// </para>
    /// </summary>
    private async Task<string?> ClaimRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        CancellationToken cancellationToken)
    {
        var key = await SerializationKeyAsync(connection, transaction, item.Id, cancellationToken);

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
                   i.workspace_id = @workspace AS "SameWorkspace"
            FROM   claim c
            JOIN   item i ON i.id = c.item_id
            LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
            WHERE  c.released_at IS NULL
              AND  (c.item_id = @id OR (c.writes AND c.serialization_key = @key))
            ORDER  BY (c.item_id = @id) DESC, c.acquired_at;
            """;

        var held = (await connection.QueryAsync<ClaimHolder>(new CommandDefinition(
            sql,
            new { id = item.Id, key, workspace = item.WorkspaceId },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        var live = held.FirstOrDefault(h => h.ItemId != item.Id || h.AgentName != actor.Name);
        if (live is null)
        {
            return null;
        }

        var you = live.AgentName == actor.Name ? " — that is you" : string.Empty;

        if (live.ItemId == item.Id)
        {
            return Refusal.Blocked(
                $"work_status({item.Ref} → in_progress). {item.Ref} is already claimed.",
                $"{live.AgentName} holds a live claim on {item.Ref}; {Lease(live)}",
                "work_next() for something else that is ready",
                $"work_status(ref:\"{item.Ref}\", to:\"in_progress\") once that lease lapses — "
                + "the sweep releases it and returns the item to ready");
        }

        if (!live.SameWorkspace)
        {
            return Refusal.Blocked(
                $"work_status({item.Ref} → in_progress). Another workspace's writer holds this working tree.",
                $"{live.QualifiedRef} in another workspace holds the write claim on "
                + $"\"{Glyph.Clip(live.SerializationKey, 40)}\" (agent {live.AgentName}{you}); {Lease(live)}",
                $"work_status(ref:\"{item.Ref}\", to:\"in_progress\") once that lease lapses and that "
                + "workspace's next work_brief, work_next or work_status sweeps it",
                "work_next(scope:\"<another run>\") — two runs in different worktrees proceed in "
                + "parallel, two sharing one working tree do not");
        }

        return Refusal.Blocked(
            $"work_status({item.Ref} → in_progress). Another writer holds this working tree.",
            $"{live.QualifiedRef} holds the write claim on \"{Glyph.Clip(live.SerializationKey, 40)}\" "
            + $"(agent {live.AgentName}{you}); {Lease(live)}",
            $"work_status(ref:\"{live.QualifiedRef}\", to:\"review\") to release it — any move out of "
            + "in_progress (review, blocked, done) releases the claim",
            $"work_status(ref:\"{item.Ref}\", to:\"in_progress\") once that lease lapses — the sweep "
            + "releases it",
            "work_next(scope:\"<another run>\") — two runs in different worktrees proceed in "
            + "parallel, two sharing one working tree do not");
    }

    private static string Lease(ClaimHolder holder)
    {
        var at = $"{holder.ExpiresAt.UtcDateTime:HH:mm} UTC";
        var left = holder.ExpiresAt - DateTimeOffset.UtcNow;

        return left > TimeSpan.Zero
            ? $"the lease lapses in {Glyph.Age(left)} ({at})"
            : $"the lease lapsed {Glyph.Age(-left)} ago ({at}) and is not swept yet";
    }

    private async Task<string> AcquireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        CancellationToken cancellationToken)
    {
        var ttl = await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken);
        var key = await SerializationKeyAsync(connection, transaction, item.Id, cancellationToken);

        const string extend =
            """
            UPDATE claim
               SET heartbeat_at = now(), expires_at = now() + make_interval(mins => @ttl)
             WHERE item_id = @item AND released_at IS NULL AND agent_name = @agent;
            """;

        var extended = await connection.ExecuteAsync(new CommandDefinition(
            extend,
            new { item = item.Id, agent = actor.Name, ttl },
            transaction,
            cancellationToken: cancellationToken));

        if (extended > 0)
        {
            return $"extended, {ttl}m left, held by {actor.Name}";
        }

        const string acquire =
            """
            INSERT INTO claim
                (id, item_id, serialization_key, writes, agent_name, role,
                 acquired_at, heartbeat_at, expires_at)
            VALUES (@id, @item, @key, true, @agent, @role::actor_role,
                    now(), now(), now() + make_interval(mins => @ttl));
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            acquire,
            new
            {
                id = Guid.CreateVersion7(),
                item = item.Id,
                key,
                agent = actor.Name,
                role = Pg.Label(actor.Role),
                ttl,
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
            $"{item.Ref} claimed by {actor.Name} for {ttl}m",
            new { agent = actor.Name, serialization_key = key, ttl_minutes = ttl },
            cancellationToken);

        return $"acquired by {actor.Name}, {ttl}m lease on \"{Glyph.Clip(key, 40)}\"";
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
               AND c.agent_name = @agent
               AND (c.item_id = i.id OR c.item_id = ANY(i.ancestors));
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { item = itemId, agent = actor.Name, ttl },
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
    /// The write lock is on the working tree, not the item: the run's <c>serialization_key</c>, else
    /// the project's repo root, else the ref scope. Walking the ancestor chain server-side keeps
    /// <c>uuid[]</c> out of the Dapper mapping, which needs a handler it does not have.
    /// </summary>
    private static async Task<string> SerializationKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT coalesce(
              (SELECT rc.serialization_key
                 FROM item i
                 CROSS JOIN LATERAL unnest(i.ancestors || i.id) WITH ORDINALITY AS u(id, ord)
                 JOIN run_config rc ON rc.item_id = u.id
                WHERE i.id = @item AND rc.serialization_key <> ''
                ORDER BY u.ord DESC LIMIT 1),
              (SELECT pc.repo_root
                 FROM item i
                 CROSS JOIN LATERAL unnest(i.ancestors || i.id) WITH ORDINALITY AS u(id, ord)
                 JOIN project_config pc ON pc.item_id = u.id
                WHERE i.id = @item AND pc.repo_root <> ''
                ORDER BY u.ord DESC LIMIT 1),
              (SELECT coalesce(s.ref, i.ref)
                 FROM item i LEFT JOIN item s ON s.id = i.ref_scope_id
                WHERE i.id = @item));
            """;

        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            sql,
            new { item = itemId },
            transaction,
            cancellationToken: cancellationToken)) ?? itemId.ToString();
    }
}
