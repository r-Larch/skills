using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>doc 14 §2 — what <c>POST /api/agent-sessions</c> asks for, already judged for its role.</summary>
public sealed record MintRequest(
    string Name,
    ActorRole Role,
    int TtlHours,
    bool Replace,
    Guid? MintedBy,
    string? LaunchedBy = null,
    string? Origin = null,
    string? Cwd = null,
    string? ExternalRole = null,
    string? ClaudeSessionId = null,
    RepoArg? Repo = null,
    IReadOnlyList<string>? Grants = null,
    string? MintedBySubject = null,
    string? MintedByAuthorizationId = null,
    Guid? UserId = null);

/// <summary>The only place the token exists in the clear; the caller returns it once and drops it.</summary>
public sealed record MintedToken(Guid SessionId, string Name, ActorRole Role, string Token, DateTimeOffset ExpiresAt);

public sealed record MintResult(MintedToken? Minted, string? Conflict);

/// <summary>A live minted token, as the bearer handler sees it.</summary>
public sealed record TokenSession(
    Guid SessionId,
    string Name,
    ActorRole Role,
    Guid WorkspaceId,
    IReadOnlyList<string> Grants,
    Guid? UserId = null);

/// <summary>What the endpoints need to judge who may PATCH or DELETE a session.</summary>
public sealed record MintedSession(Guid Id, string Name, ActorRole? GrantedRole, Guid? MintedBySessionId, Guid? UserId = null);

public static class SessionToken
{
    public const string Prefix = "wss_";

    public static string New() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 over the token's UTF-8 bytes, prefix included, as lowercase hex.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed partial class SessionStore
{
    public const int DefaultTtlHours = 72;
    public const int MaxTtlHours = 720;

    private sealed record MintRow(Guid Id, string? ClaudeSessionId, string? LaunchedBy, bool Live);
    private sealed record TokenRow(Guid SessionId, string Name, string Role, Guid WorkspaceId, string Grants, Guid? UserId);
    private sealed record BoundRow(Guid WorkspaceId, string Name, string? ClaudeSessionId);
    private sealed record FoundRow(Guid Id, string Name, string? Role, Guid? MintedBy, Guid? UserId);

    /// <summary>
    /// The minting user's check on every use, as <c>UserStore.EnterAsync</c> makes it for a cookie: the user
    /// exists, is not disabled, and is a member of the session's workspace or a system administrator.
    /// </summary>
    private const string UserMayEnter =
        """
        (s.user_id IS NULL OR EXISTS (
            SELECT 1 FROM app_user u
             WHERE u.id = s.user_id AND u.disabled_at IS NULL
               AND (u.is_system_admin OR EXISTS (
                       SELECT 1 FROM tenant_membership m WHERE m.user_id = u.id AND m.workspace_id = s.workspace_id))))
        """;

    /// <summary>
    /// One row per (user, name), so a mint for a name that exists — a header-named session or an earlier
    /// mint by the same user — lands on that row and keeps its id, claims and history; <c>replace</c> swaps
    /// only the token. The launcher collision rule of doc 11 holds here as it does for headers.
    /// </summary>
    public async Task<MintResult> MintAsync(
        Guid workspaceId,
        MintRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        var launchedBy = SessionFacts.Clean(request.LaunchedBy);
        var claudeId = SessionFacts.Clean(request.ClaudeSessionId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string upsert =
            """
            INSERT INTO agent_session (id, workspace_id, user_id, name, role, launched_by)
            VALUES (@id, @workspace, @user, @name, @role::actor_role, @launchedBy)
            ON CONFLICT (workspace_id, user_id, name) DO NOTHING;

            SELECT id                AS "Id",
                   claude_session_id AS "ClaudeSessionId",
                   launched_by       AS "LaunchedBy",
                   (token_hash IS NOT NULL AND revoked_at IS NULL AND token_expires_at > now()) AS "Live"
            FROM   agent_session
            WHERE  workspace_id = @workspace AND user_id IS NOT DISTINCT FROM @user AND name = @name
            FOR    UPDATE;
            """;

        var row = await connection.QuerySingleAsync<MintRow>(new CommandDefinition(
            upsert,
            new { id = Guid.CreateVersion7(), workspace = workspaceId, user = request.UserId, name, role = Pg.Label(request.Role), launchedBy },
            transaction,
            cancellationToken: cancellationToken));

        // doc 11 — a launcher may adopt a launcher-less row only by proving it is the same Claude session.
        var takeover = request.Replace
            && launchedBy is not null
            && row.LaunchedBy is null
            && row.ClaudeSessionId is not null
            && string.Equals(row.ClaudeSessionId, claudeId, StringComparison.Ordinal);

        if (!takeover && !string.Equals(row.LaunchedBy, launchedBy, StringComparison.Ordinal))
        {
            return new MintResult(null,
                $"the session name \"{name}\" is held by a session launched by {row.LaunchedBy ?? "no launcher"}, "
                + $"and this mint says {launchedBy ?? "no launcher"}; two launchers' sessions cannot share a name, "
                + "and replace does not override that. A launcher takes over a name with no launcher only with "
                + "replace: true and the claudeSessionId that row records. If that session is gone, a controller "
                + $"frees the name with {ReleasePath(row.Id)} (or Release on the dashboard's Sessions page), "
                + "and the next mint creates it fresh");
        }

        if (row.Live && !request.Replace)
        {
            return new MintResult(null,
                $"the session \"{name}\" already has a live token; pass replace: true to revoke it and mint a new one");
        }

        var token = SessionToken.New();

        const string mint =
            """
            UPDATE agent_session s
               SET token_hash           = @hash,
                   granted_role         = @role::actor_role,
                   token_expires_at     = now() + make_interval(hours => @ttl),
                   token_ttl_hours      = @ttl,
                   revoked_at           = NULL,
                   minted_by_session_id = @mintedBy,
                   minted_by_subject    = @mintedBySubject,
                   minted_by_authorization_id = @mintedByAuthorization,
                   role                 = @role::actor_role,
                   launched_by          = @launchedBy,
                   claude_session_id    = coalesce(@claude, s.claude_session_id),
                   external_role        = CASE WHEN @external IS NULL OR @external = @role THEN s.external_role
                                               ELSE @external END,
                   origin               = coalesce(@origin, s.origin),
                   cwd                  = coalesce(@cwd, s.cwd),
                   repo_remote          = coalesce(@remote, s.repo_remote),
                   repo_root            = coalesce(@root, s.repo_root),
                   grants               = coalesce(string_to_array(nullif(@grants, ''), ','), '{}'),
                   last_seen_at         = now()
             WHERE s.id = @id
            RETURNING token_expires_at;
            """;

        var expiresAt = await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition(
            mint,
            new
            {
                id = row.Id,
                hash = SessionToken.Hash(token),
                role = Pg.Label(request.Role),
                ttl = request.TtlHours,
                launchedBy,
                mintedBy = request.MintedBy,
                mintedBySubject = request.MintedBySubject,
                mintedByAuthorization = request.MintedByAuthorizationId,
                claude = claudeId,
                external = SessionFacts.Clean(request.ExternalRole),
                origin = SessionFacts.Clean(request.Origin),
                cwd = SessionFacts.Clean(request.Cwd),
                remote = RepoKeys.NormaliseRemote(request.Repo?.Remote),
                root = RepoKeys.NormaliseRoot(request.Repo?.Root),
                grants = string.Join(',', request.Grants ?? []),
            },
            transaction,
            cancellationToken: cancellationToken));

        await ClaudeIdChangedAsync(
            connection, transaction, workspaceId, row.Id, name, row.ClaudeSessionId, claudeId, request.Role, cancellationToken);

        if (takeover)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary, session_id)
                VALUES (@workspace, NULL, @actor, @role::actor_role, 'session_launcher_taken_over',
                        @payload::jsonb, @summary, @session);
                """,
                new
                {
                    workspace = workspaceId,
                    actor = Pg.Label(request.Role),
                    role = Pg.Label(request.Role),
                    payload = JsonSerializer.Serialize(new { from = (string?)null, to = launchedBy, claudeSessionId = claudeId }),
                    summary = Glyph.Flat($"session {name} taken over by launcher {launchedBy}, same Claude session {claudeId}"),
                    session = row.Id,
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        return new MintResult(new MintedToken(row.Id, name, request.Role, token, expiresAt), null);
    }

    /// <summary>
    /// A token that is unknown, revoked or expired finds nothing, and the three read alike. A live one
    /// slides its expiry to now plus its own window, written at most once a minute; a concurrent use
    /// waits on the row, re-reads it renewed, skips the write and still authenticates.
    /// <para>
    /// A token minted with an OAuth token lives only as long as that OAuth authorization is valid, read on
    /// every use with no cache: revoked or pruned, the token reads as unknown from the next call on. A token a
    /// user minted works only while that user may enter its workspace (<see cref="UserMayEnter"/>), likewise.
    /// </para>
    /// </summary>
    public async Task<TokenSession?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
            UPDATE agent_session s
               SET token_expires_at = now() + make_interval(hours => coalesce(s.token_ttl_hours, @defaultTtl))
             WHERE s.token_hash = @hash
               AND s.revoked_at IS NULL
               AND s.token_expires_at > now()
               AND s.token_expires_at < now() + make_interval(hours => coalesce(s.token_ttl_hours, @defaultTtl))
                                              - interval '1 minute'
               AND (s.minted_by_authorization_id IS NULL OR EXISTS (
                       SELECT 1 FROM openiddict_authorization a
                        WHERE a.id = s.minted_by_authorization_id AND a.status = 'valid'))
               AND {UserMayEnter};

            SELECT s.id                 AS "SessionId",
                   s.name               AS "Name",
                   s.granted_role::text AS "Role",
                   s.workspace_id       AS "WorkspaceId",
                   array_to_string(s.grants, ',') AS "Grants",
                   s.user_id            AS "UserId"
            FROM   agent_session s
            WHERE  s.token_hash = @hash
              AND  s.revoked_at IS NULL
              AND  s.token_expires_at > now()
              AND  (s.minted_by_authorization_id IS NULL OR EXISTS (
                       SELECT 1 FROM openiddict_authorization a
                        WHERE a.id = s.minted_by_authorization_id AND a.status = 'valid'))
              AND  {UserMayEnter};
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TokenRow>(
            new CommandDefinition(
                sql,
                new { hash = SessionToken.Hash(token), defaultTtl = DefaultTtlHours },
                cancellationToken: cancellationToken));

        return row is { } r
            ? new TokenSession(
                r.SessionId,
                r.Name,
                Enum.Parse<ActorRole>(r.Role, ignoreCase: true),
                r.WorkspaceId,
                r.Grants.Split(',', StringSplitOptions.RemoveEmptyEntries),
                r.UserId)
            : null;
    }

    /// <summary>
    /// The per-call resolution for a minted token: the session is the credential's, so no header
    /// names it and the launcher rule has nothing to judge. The row is touched and its leases renewed
    /// exactly as for a header-named session.
    /// </summary>
    public async Task<SessionRef?> ResolveBoundAsync(
        Guid sessionId,
        ActorRole role,
        SessionFacts facts,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<BoundRow>(
            new CommandDefinition(
                """
                SELECT workspace_id AS "WorkspaceId", name AS "Name", claude_session_id AS "ClaudeSessionId"
                FROM   agent_session WHERE id = @id
                FOR    UPDATE;
                """,
                new { id = sessionId },
                transaction,
                cancellationToken: cancellationToken));

        if (row is not { } session)
        {
            return null;
        }

        await TouchAsync(connection, transaction, sessionId, role, facts, cancellationToken);
        await ClaudeIdChangedAsync(
            connection, transaction, session.WorkspaceId, sessionId, session.Name, session.ClaudeSessionId,
            SessionFacts.Clean(facts.ClaudeSessionId), role, cancellationToken);
        await RenewAsync(connection, transaction, session.WorkspaceId, sessionId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SessionRef(sessionId, session.Name);
    }

    public async Task<MintedSession?> FindAsync(Guid workspaceId, Guid id, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT id AS "Id", name AS "Name", granted_role::text AS "Role", minted_by_session_id AS "MintedBy",
                   user_id AS "UserId"
            FROM   agent_session
            WHERE  id = @id AND workspace_id = @workspace;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<FoundRow>(
            new CommandDefinition(sql, new { id, workspace = workspaceId }, cancellationToken: cancellationToken));

        return row is { } r
            ? new MintedSession(
                r.Id,
                r.Name,
                r.Role is null ? null : Enum.Parse<ActorRole>(r.Role, ignoreCase: true),
                r.MintedBy,
                r.UserId)
            : null;
    }

    /// <summary>doc 11 — a change of Claude session under one name is kept as history.</summary>
    public async Task SetClaudeSessionAsync(
        Guid sessionId,
        string claudeSessionId,
        ActorRole role,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var row = await connection.QuerySingleAsync<BoundRow>(
            new CommandDefinition(
                """
                SELECT workspace_id AS "WorkspaceId", name AS "Name", claude_session_id AS "ClaudeSessionId"
                FROM   agent_session WHERE id = @id
                FOR    UPDATE;
                """,
                new { id = sessionId },
                transaction,
                cancellationToken: cancellationToken));

        var claudeId = claudeSessionId.Trim();

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_session SET claude_session_id = @claudeId WHERE id = @id;",
            new { id = sessionId, claudeId },
            transaction,
            cancellationToken: cancellationToken));

        await ClaudeIdChangedAsync(
            connection, transaction, row.WorkspaceId, sessionId, row.Name, row.ClaudeSessionId, claudeId, role, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// doc 14 §2 — the token stops authenticating and the session lets go of what it held. A
    /// released claim is handled as an expired one is (<see cref="Housekeeping.SweepAsync"/>): its
    /// item returns to <c>ready</c> and the journal says why, so the frontier offers it again.
    /// </summary>
    public async Task RevokeAsync(Guid sessionId, ActorRole role, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            UPDATE agent_session SET revoked_at = coalesce(revoked_at, now()) WHERE id = @session;

            WITH released AS (
              UPDATE claim c
                 SET released_at = now(), release_reason = 'revoked'
                FROM item i
               WHERE i.id = c.item_id
                 AND c.session_id = @session
                 AND c.released_at IS NULL
              RETURNING c.item_id, i.workspace_id, i.ref
            ),
            returned AS (
              UPDATE item i
                 SET status = 'ready'
                FROM released r
               WHERE i.id = r.item_id AND i.status = 'in_progress'
              RETURNING i.id
            )
            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary, session_id)
            SELECT r.workspace_id, r.item_id, @actor, @role::actor_role, 'claim_revoked',
                   jsonb_build_object('session', @name::text),
                   r.ref || ' claim by ' || @name::text || ' released — session token revoked',
                   @session
            FROM   released r;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var name = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT name FROM agent_session WHERE id = @session FOR UPDATE;",
            new { session = sessionId },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { session = sessionId, actor = Pg.Label(role), role = Pg.Label(role), name },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }
}
