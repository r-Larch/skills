using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>
/// doc 11 — self-naming. A hand-started session shares the static registration and so has no name
/// of its own; the session helper sends <c>X-WorkState-Client</c>, a key for the Claude Code process
/// it runs under, and <c>work_session(name)</c> binds that key to a row. Every later call carrying
/// only the key resolves to that row.
/// </summary>
public sealed partial class SessionStore
{
    private sealed record KeyRow(Guid Id, string Name, string? ClaudeSessionId);

    private sealed record BindRow(
        Guid Id,
        string? ClaudeSessionId,
        string? LaunchedBy,
        string? ClientKey,
        bool Minted,
        bool Live,
        DateTimeOffset LastSeenAt);

    /// <summary>
    /// A key nobody bound yet resolves to nothing, and the call is unnamed as before; so does a key another
    /// user's session holds, since <paramref name="userId"/>'s calls are never signed with someone else's name.
    /// </summary>
    public async Task<SessionRef?> ResolveByKeyAsync(
        Guid workspaceId,
        Guid? userId,
        ActorRole role,
        SessionFacts facts,
        CancellationToken cancellationToken = default)
    {
        if (SessionFacts.Clean(facts.ClientKey) is not { } key)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<KeyRow>(new CommandDefinition(
            """
            SELECT id AS "Id", name AS "Name", claude_session_id AS "ClaudeSessionId"
            FROM   agent_session
            WHERE  workspace_id = @workspace AND client_key = @key AND user_id IS NOT DISTINCT FROM @user
            FOR    UPDATE;
            """,
            new { workspace = workspaceId, key, user = userId },
            transaction,
            cancellationToken: cancellationToken));

        if (row is not { } session)
        {
            return null;
        }

        await TouchAsync(connection, transaction, session.Id, role, facts, cancellationToken);
        await ClaudeIdChangedAsync(
            connection, transaction, workspaceId, session.Id, session.Name, session.ClaudeSessionId,
            SessionFacts.Clean(facts.ClaudeSessionId), role, cancellationToken);
        await RenewAsync(connection, transaction, workspaceId, session.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SessionRef(session.Id, session.Name);
    }

    /// <summary>
    /// <c>work_session</c>: binds the key to the row for <c>name</c>, creating it as a header would.
    /// A name another key used within the lease window is still that session's; a minted session's
    /// name belongs to its credential; the launcher rule holds as it does for headers.
    /// </summary>
    public async Task<SessionResolution> BindAsync(
        Guid workspaceId,
        Guid? userId,
        ActorRole role,
        string name,
        SessionFacts facts,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (SessionFacts.Clean(facts.ClientKey) is not { } key)
        {
            return new SessionResolution(null, NoKey());
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string upsert =
            """
            INSERT INTO agent_session (id, workspace_id, user_id, name, role)
            VALUES (@id, @workspace, @user, @name, @role::actor_role)
            ON CONFLICT (workspace_id, user_id, name) DO NOTHING;

            SELECT id                AS "Id",
                   claude_session_id AS "ClaudeSessionId",
                   launched_by       AS "LaunchedBy",
                   client_key        AS "ClientKey",
                   (token_hash IS NOT NULL AND revoked_at IS NULL AND token_expires_at > now()) AS "Minted",
                   last_seen_at > now() - make_interval(mins => (
                     SELECT CASE WHEN settings->>'claim_ttl_minutes' ~ '^[0-9]+$'
                                      AND (settings->>'claim_ttl_minutes')::int > 0
                                 THEN (settings->>'claim_ttl_minutes')::int
                                 ELSE @fallback END
                     FROM workspace WHERE id = @workspace)) AS "Live",
                   last_seen_at      AS "LastSeenAt"
            FROM   agent_session
            WHERE  workspace_id = @workspace AND user_id IS NOT DISTINCT FROM @user AND name = @name
            FOR    UPDATE;
            """;

        var row = await connection.QuerySingleAsync<BindRow>(new CommandDefinition(
            upsert,
            new
            {
                id = Guid.CreateVersion7(),
                workspace = workspaceId,
                user = userId,
                name,
                role = Pg.Label(role),
                fallback = WorkWriter.DefaultTtlMinutes,
            },
            transaction,
            cancellationToken: cancellationToken));

        if (row.LaunchedBy is not null)
        {
            return new SessionResolution(null, Collision(name, row.LaunchedBy, null));
        }

        if (row.Minted)
        {
            return new SessionResolution(null, Taken(name, "a minted session's: its name comes from its token"));
        }

        if (row.ClientKey is { } held && held != key && row.Live)
        {
            return new SessionResolution(null, Taken(
                name, $"held by another live session, last seen {Glyph.Ago(DateTimeOffset.UtcNow - row.LastSeenAt)}"));
        }

        await BindKeyAsync(connection, transaction, workspaceId, row.Id, key, cancellationToken);
        await TouchAsync(connection, transaction, row.Id, role, facts, cancellationToken);
        await ClaudeIdChangedAsync(
            connection, transaction, workspaceId, row.Id, name, row.ClaudeSessionId,
            SessionFacts.Clean(facts.ClaudeSessionId), role, cancellationToken);
        await RenewAsync(connection, transaction, workspaceId, row.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SessionResolution(new SessionRef(row.Id, name), null);
    }

    /// <summary>A key names one session: binding it here takes it off any other row.</summary>
    private static Task BindKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid sessionId,
        string key,
        CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE agent_session SET client_key = NULL
             WHERE workspace_id = @workspace AND client_key = @key AND id <> @id;

            UPDATE agent_session SET client_key = @key
             WHERE id = @id AND client_key IS DISTINCT FROM @key;
            """,
            new { workspace = workspaceId, id = sessionId, key },
            transaction,
            cancellationToken: cancellationToken));

    public static string NoKey()
        => Refusal.Blocked(
            "this session sends no X-WorkState-Client header.",
            "work_session binds the key the session helper sends; without it there is nothing to name",
            "/rlarch:workstate-setup (it installs the session helper), then start a new session");

    private static string Taken(string name, string why)
        => Refusal.Blocked(
            $"the session name \"{name}\" is taken.",
            $"it is {why}. Nothing ran",
            "work_session(name:\"<another name>\") — e.g. the name with a suffix");
}
