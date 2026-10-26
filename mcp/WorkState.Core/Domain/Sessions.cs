namespace WorkState.Core.Domain;

/// <summary>
/// doc 11 — what the session that made a call is called. A label beside the role, never a source
/// of it: the role is the bearer token's and nothing else (doc 01 §6).
/// <para>
/// Keyed by (workspace, name) and nothing else, so a resume — a new MCP connection under the same
/// name — lands on the same row with its claims and its tree. The transport is stateless, so there
/// is no connection id to key an unnamed call by: an unnamed call has no session.
/// </para>
/// </summary>
public class AgentSession
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Unique per workspace.</summary>
    public string Name { get; set; } = "";

    public string? ClaudeSessionId { get; set; }

    /// <summary>The token's role at the last call — recorded, never consulted.</summary>
    public ActorRole Role { get; set; }

    /// <summary><c>X-WorkState-Foreman-Role</c>, when it differs from the token role.</summary>
    public string? ExternalRole { get; set; }

    public string? Origin { get; set; }
    public string? LaunchedBy { get; set; }
    public string? Cwd { get; set; }

    /// <summary>The tree this session last claimed on.</summary>
    public string? Tree { get; set; }

    public string? ClientName { get; set; }
    public string? ClientVersion { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// doc 14 §2 — SHA-256 (lowercase hex) of the session's minted <c>wss_</c> token. The token
    /// itself is returned once and stored nowhere. Null for a session only ever named by header.
    /// </summary>
    public string? TokenHash { get; set; }

    /// <summary>The role the minted token carries; unlike <see cref="Role"/>, this one is consulted.</summary>
    public ActorRole? GrantedRole { get; set; }

    /// <summary>The current expiry: each use slides it to now plus <see cref="TokenTtlHours"/>, with no cap.</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>The idle window the token was minted with; null on a row minted before it was stored, read as 72.</summary>
    public int? TokenTtlHours { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The minted session that minted this one; null when a static token did.</summary>
    public Guid? MintedBySessionId { get; set; }

    /// <summary>The repository the session was launched in, normalised by <see cref="RepoKeys"/>.</summary>
    public string? RepoRemote { get; set; }

    public string? RepoRoot { get; set; }

    /// <summary>
    /// Capabilities beside the role, each one hiding or showing a tool. Set only by a mint, by an
    /// orchestrator or controller; <see cref="SessionGrants.Import"/> is the one there is.
    /// </summary>
    public string[] Grants { get; set; } = [];
}

public static class SessionGrants
{
    public const string Import = "import";

    public static readonly IReadOnlyList<string> All = [Import];
}
