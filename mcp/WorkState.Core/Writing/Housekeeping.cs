using Dapper;
using Npgsql;

namespace WorkState.Core.Writing;

/// <summary>
/// The two writes that happen as a side effect of a read. U3 could surface an expired lease but not
/// release one, and a note that is rendered into a brief and never marked consumed is rendered into
/// the next brief too — so both live here and are called from <c>work_brief</c>, <c>work_next</c>
/// and <c>work_status</c> (doc 07 §5).
/// </summary>
public sealed class Housekeeping(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// doc 07 §5. The unique index makes an overlapping claim impossible; this makes an expired one
    /// <em>visible</em>: the lease is released, a <c>claim_expired</c> event records it, and the
    /// item returns to <c>ready</c> so the frontier offers it again. A parked agent stops writing,
    /// its lease lapses, and the store says so.
    /// </summary>
    public async Task<int> SweepAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            WITH expired AS (
              UPDATE claim c
                 SET released_at = now(), release_reason = 'expired'
                FROM item i
               WHERE i.id = c.item_id
                 AND i.workspace_id = @workspace
                 AND c.released_at IS NULL
                 AND c.expires_at < now()
              RETURNING c.item_id, c.agent_name, c.expires_at, i.ref
            ),
            returned AS (
              UPDATE item i
                 SET status = 'ready'
                FROM expired e
               WHERE i.id = e.item_id AND i.status = 'in_progress'
              RETURNING i.id
            )
            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary)
            SELECT @workspace, e.item_id, e.agent_name, 'system'::actor_role, 'claim_expired',
                   jsonb_build_object('agent', e.agent_name, 'expired_at', e.expires_at),
                   e.ref || ' claim by ' || e.agent_name || ' expired — returned to ready'
            FROM   expired e;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            new CommandDefinition(sql, new { workspace = workspaceId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// doc 01 §3.4 — a note addressed to an item is injected into that item's brief and then marked
    /// consumed. Called only after the brief has rendered them, so the agent that pays for the call
    /// is the one that receives them.
    /// </summary>
    public async Task<int> ConsumeNotesAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            UPDATE note SET consumed_at = now()
             WHERE to_item_id = @item AND consumed_at IS NULL;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            new CommandDefinition(sql, new { item = itemId }, cancellationToken: cancellationToken));
    }
}
