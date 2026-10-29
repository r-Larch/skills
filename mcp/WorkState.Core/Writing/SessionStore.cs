using System.Text.Json;
using Dapper;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>
/// What one call's registration headers say about the session it comes from (doc 11). None of it
/// is ever consulted for the role.
/// </summary>
public sealed record SessionFacts(
    string? Name = null,
    string? ClaudeSessionId = null,
    string? LaunchedBy = null,
    string? Origin = null,
    string? Cwd = null,
    string? ExternalRole = null,
    string? ClientName = null,
    string? ClientVersion = null,
    string? ClientKey = null)
{
    public static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The session a call resolved to.</summary>
public sealed record SessionRef(Guid Id, string Name);

public sealed record SessionResolution(SessionRef? Session, string? Refused);

/// <summary>
/// doc 11 — the <c>agent_session</c> rows. Called once per tool call, before the tool runs: it
/// resolves the caller's row by (workspace, user, name), touches <c>last_seen_at</c>, and renews the leases
/// that session holds. A call with no name has no session and is attributed as it always was.
/// </summary>
public sealed partial class SessionStore(NpgsqlDataSource dataSource)
{
    private readonly WorkStateConnections _connections = new(dataSource);

    private sealed record Row(Guid Id, string? ClaudeSessionId, string? LaunchedBy);

    /// <summary>A caller with no user — a static token — names its sessions in the workspace's shared namespace.</summary>
    public Task<SessionResolution> ResolveAsync(
        Guid workspaceId,
        ActorRole role,
        SessionFacts facts,
        CancellationToken cancellationToken = default)
        => ResolveAsync(workspaceId, null, role, facts, cancellationToken);

    /// <param name="userId">The signed-in user the credential belongs to, whose names are their own.</param>
    public async Task<SessionResolution> ResolveAsync(
        Guid workspaceId,
        Guid? userId,
        ActorRole role,
        SessionFacts facts,
        CancellationToken cancellationToken = default)
    {
        if (SessionFacts.Clean(facts.Name) is not { } name)
        {
            return new SessionResolution(null, null);
        }

        var launchedBy = SessionFacts.Clean(facts.LaunchedBy);
        var claudeId = SessionFacts.Clean(facts.ClaudeSessionId);

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        // The first calls of one session can arrive together; the insert must not race itself.
        const string upsert =
            """
            INSERT INTO agent_session (id, workspace_id, user_id, name, role, launched_by)
            VALUES (@id, @workspace, @user, @name, @role::actor_role, @launchedBy)
            ON CONFLICT (workspace_id, user_id, name) DO NOTHING;

            SELECT id                AS "Id",
                   claude_session_id AS "ClaudeSessionId",
                   launched_by       AS "LaunchedBy"
            FROM   agent_session
            WHERE  workspace_id = @workspace AND user_id IS NOT DISTINCT FROM @user AND name = @name
            FOR    UPDATE;
            """;

        var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(
            upsert,
            new { id = Guid.CreateVersion7(), workspace = workspaceId, user = userId, name, role = Pg.Label(role), launchedBy },
            transaction,
            cancellationToken: cancellationToken));

        if (!string.Equals(row.LaunchedBy, launchedBy, StringComparison.Ordinal))
        {
            return new SessionResolution(null, Collision(name, row.LaunchedBy, launchedBy));
        }

        if (SessionFacts.Clean(facts.ClientKey) is { } key)
        {
            await BindKeyAsync(connection, transaction, workspaceId, row.Id, key, cancellationToken);
        }

        await TouchAsync(connection, transaction, row.Id, role, facts, cancellationToken);
        await ClaudeIdChangedAsync(
            connection, transaction, workspaceId, row.Id, name, row.ClaudeSessionId, claudeId, role, cancellationToken);

        await RenewAsync(connection, transaction, workspaceId, row.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SessionResolution(new SessionRef(row.Id, name), null);
    }

    /// <summary>
    /// Kept as history rather than overwritten: two Claude sessions answering to one name is how a
    /// copy shows itself.
    /// </summary>
    private static async Task ClaudeIdChangedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid sessionId,
        string name,
        string? previous,
        string? claudeId,
        ActorRole role,
        CancellationToken cancellationToken)
    {
        if (claudeId is null || previous is null || string.Equals(previous, claudeId, StringComparison.Ordinal))
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary, session_id)
            VALUES (@workspace, NULL, @actor, @role::actor_role, 'session_claude_id_changed',
                    @payload::jsonb, @summary, @session);
            """,
            new
            {
                workspace = workspaceId,
                actor = Pg.Label(role),
                role = Pg.Label(role),
                payload = JsonSerializer.Serialize(new { from = previous, to = claudeId }),
                summary = Glyph.Flat($"session {name} is now Claude session {claudeId}"),
                session = sessionId,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static string Collision(string name, string? held, string? asked)
        => Refusal.Blocked(
            $"the session name \"{name}\" is held by a session launched by {held ?? "no launcher"}.",
            $"this call's X-WorkState-Launched-By says {asked ?? "no launcher"}; a name is unique per "
            + "workspace and user, and two launchers' sessions cannot share one. Nothing ran",
            "work_brief() once WorkState is registered with X-WorkState-Agent:<a name of your own> — "
            + "the name your session goes by, e.g. the one ListAgents prints",
            "work_brief() once X-WorkState-Launched-By names the launcher that started this session, "
            + "if it is the same one");

    private static async Task TouchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        ActorRole role,
        SessionFacts facts,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE agent_session s
               SET claude_session_id = coalesce(@claude, s.claude_session_id),
                   role              = @role::actor_role,
                   external_role     = CASE WHEN @external IS NULL OR @external = @role THEN s.external_role
                                            ELSE @external END,
                   origin            = coalesce(@origin, s.origin),
                   cwd               = coalesce(@cwd, s.cwd),
                   client_name       = coalesce(@clientName, s.client_name),
                   client_version    = coalesce(@clientVersion, s.client_version),
                   last_seen_at      = now()
             WHERE s.id = @id;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id,
                claude = SessionFacts.Clean(facts.ClaudeSessionId),
                role = Pg.Label(role),
                external = SessionFacts.Clean(facts.ExternalRole),
                origin = SessionFacts.Clean(facts.Origin),
                cwd = SessionFacts.Clean(facts.Cwd),
                clientName = SessionFacts.Clean(facts.ClientName),
                clientVersion = SessionFacts.Clean(facts.ClientVersion),
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// doc 11 — any call from the claiming session renews its live claims. A lease that lapses under
    /// a healthy multi-hour unit is the "claim expired" the inbox showed for work still going on.
    /// </summary>
    private static async Task RenewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE claim
               SET heartbeat_at = now(),
                   expires_at   = now() + make_interval(mins => (
                     SELECT CASE WHEN settings->>'claim_ttl_minutes' ~ '^[0-9]+$'
                                      AND (settings->>'claim_ttl_minutes')::int > 0
                                 THEN (settings->>'claim_ttl_minutes')::int
                                 ELSE @fallback END
                     FROM workspace WHERE id = @workspace))
             WHERE session_id = @session AND released_at IS NULL;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { workspace = workspaceId, session = sessionId, fallback = WorkWriter.DefaultTtlMinutes },
            transaction,
            cancellationToken: cancellationToken));
    }
}
