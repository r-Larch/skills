using System.Text.Json;
using Dapper;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

public enum ReleaseOutcome
{
    Released,
    NotFound,
    AlreadyReleased,
    Holding,
}

/// <summary><see cref="Tombstone"/> is the row's name from now on; <see cref="Held"/> the refs that refused it.</summary>
public sealed record ReleaseResult(ReleaseOutcome Outcome, string? Name = null, string? Tombstone = null, string? Held = null);

public sealed partial class SessionStore
{
    /// <summary>The marker a released row's name ends with; its own id makes it unique.</summary>
    public const string TombstoneMarker = "~released~";

    public static string ReleasePath(Guid sessionId) => $"POST /api/agent-sessions/{sessionId}/release";

    public static string TombstoneName(string name, Guid sessionId) => $"{name}{TombstoneMarker}{sessionId:n}";

    private sealed record ReleaseRow(string Name, string? Held);

    /// <summary>
    /// doc 11 — frees a session name for the next mint without deleting the row: claims, events and
    /// evidence keep pointing at it under a tombstone name, its token is revoked and its client key let
    /// go. A session that still holds live claims is refused; revoking it releases them first.
    /// </summary>
    public async Task<ReleaseResult> ReleaseNameAsync(
        Guid workspaceId,
        Guid sessionId,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<ReleaseRow>(new CommandDefinition(
            """
            SELECT s.name AS "Name",
                   (SELECT string_agg(i.ref, ', ' ORDER BY i.ref)
                    FROM   claim c JOIN item i ON i.id = c.item_id
                    WHERE  c.session_id = s.id AND c.released_at IS NULL AND c.expires_at > now()) AS "Held"
            FROM   agent_session s
            WHERE  s.id = @id AND s.workspace_id = @workspace
            FOR    UPDATE OF s;
            """,
            new { id = sessionId, workspace = workspaceId },
            transaction,
            cancellationToken: cancellationToken));

        if (row is null)
        {
            return new ReleaseResult(ReleaseOutcome.NotFound);
        }

        var tombstone = TombstoneName(row.Name, sessionId);
        if (row.Name.EndsWith($"{TombstoneMarker}{sessionId:n}", StringComparison.Ordinal))
        {
            return new ReleaseResult(ReleaseOutcome.AlreadyReleased, row.Name);
        }

        if (row.Held is { } held)
        {
            return new ReleaseResult(ReleaseOutcome.Holding, row.Name, Held: held);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE agent_session
               SET name       = @tombstone,
                   revoked_at = coalesce(revoked_at, now()),
                   client_key = NULL
             WHERE id = @session;

            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary, session_id)
            VALUES (@workspace, NULL, @actor, @role::actor_role, 'session_name_released',
                    @payload::jsonb, @summary, @session);
            """,
            new
            {
                session = sessionId,
                workspace = workspaceId,
                tombstone,
                actor = actor.Name,
                role = Pg.Label(actor.Role),
                payload = JsonSerializer.Serialize(new { name = row.Name, tombstone }),
                summary = Glyph.Flat($"session name {row.Name} released by {actor.Label}; the row is now {tombstone}"),
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return new ReleaseResult(ReleaseOutcome.Released, row.Name, tombstone);
    }
}
