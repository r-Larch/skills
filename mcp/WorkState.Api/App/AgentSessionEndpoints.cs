using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkState.Api.Auth;
using WorkState.Api.Auth.OAuth;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Mcp;

namespace WorkState.Api.App;

public sealed record MintSessionRequest(
    string? Name,
    string? Role,
    string? LaunchedBy = null,
    string? Origin = null,
    string? Cwd = null,
    string? ClaudeSessionId = null,
    string? ExternalRole = null,
    int? TtlHours = null,
    bool? Replace = null,
    RepoArg? Repo = null,
    string[]? Grants = null);

public sealed record MintSessionResponse(Guid SessionId, string Name, string Role, string Token, DateTimeOffset ExpiresAt);

public sealed record PatchSessionRequest(string? ClaudeSessionId);

/// <summary><c>name</c> is the name now free; <c>releasedAs</c> what the row is called from here on.</summary>
public sealed record ReleaseSessionResponse(Guid SessionId, string Name, string ReleasedAs);

/// <summary>
/// doc 14 §2 — per-session tokens. A write surface for launchers, so it sits beside the App API and
/// not under <c>/api/v1</c>, which is read-only by rule. Every call needs an orchestrator or
/// controller bearer, or an OAuth token for <c>/api</c> with <c>workstate.sessions</c>; a worker is
/// forbidden and the dashboard cookie is not a bearer at all. A session a user's credential mints is
/// that user's: its name is unique among their sessions, and it ends when they may no longer enter.
/// </summary>
public static class AgentSessionEndpoints
{
    public const string Policy = "agent-sessions";

    public static void MapAgentSessions(this RouteGroupBuilder api)
    {
        var sessions = api.MapGroup("/agent-sessions").RequireAuthorization(Policy);

        sessions.MapPost("", MintAsync);
        sessions.MapPatch("/{id:guid}", PatchAsync);
        sessions.MapDelete("/{id:guid}", RevokeAsync);
        sessions.MapPost("/{id:guid}/release", ReleaseByBearerAsync);
    }

    /// <summary>The dashboard's Release: the operator, by cookie, under the same guard as archiving.</summary>
    public static void MapSessionRelease(this RouteGroupBuilder api)
    {
        api.MapGroup("/sessions").DashboardWrite()
            .MapPost("/{id:guid}/release", (Guid id, HttpContext context, [FromServices] SessionStore store,
                    [FromServices] RequestTenant tenant, CancellationToken cancellationToken)
                => ReleaseAsync(
                    id,
                    new Actor(context.User.Identity?.Name ?? DashboardAuth.Role, ActorRole.Human),
                    store,
                    tenant,
                    cancellationToken));
    }

    private static async Task<IResult> MintAsync(
        MintSessionRequest request,
        HttpContext context,
        [FromServices] SessionStore store,
        [FromServices] RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Error(StatusCodes.Status400BadRequest, "name is required");
        }

        if (ParseRole(request.Role) is not { } role)
        {
            return Error(StatusCodes.Status400BadRequest, "role must be worker, orchestrator or controller");
        }

        var ttl = request.TtlHours ?? SessionStore.DefaultTtlHours;
        if (ttl is < 1 or > SessionStore.MaxTtlHours)
        {
            return Error(StatusCodes.Status400BadRequest, $"ttlHours must be between 1 and {SessionStore.MaxTtlHours}");
        }

        var caller = Rank(context.User);
        if (Rank(role) > Rank(caller))
        {
            return Error(
                StatusCodes.Status403Forbidden,
                $"a {Pg(caller)} cannot mint a {Pg(role)} token; a role is never raised");
        }

        var grants = (request.Grants ?? []).Select(g => g?.Trim().ToLowerInvariant() ?? string.Empty).Distinct().ToArray();
        if (grants.FirstOrDefault(g => !SessionGrants.All.Contains(g)) is { } unknown)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                $"\"{unknown}\" is not a grant; grants takes {string.Join(", ", SessionGrants.All)}");
        }

        // work_import writes plan items, which is an orchestrator's act; a worker holding the grant
        // would see a tool that writes what its own role cannot.
        if (grants.Contains(SessionGrants.Import) && Rank(role) < Rank(ActorRole.Orchestrator))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "the import grant needs role orchestrator or controller; mint role: \"orchestrator\", grants: [\"import\"]");
        }

        // Into the caller's own tenant, never one the request names, and under the caller's user when it has one.
        var result = await store.MintAsync(
            tenant.Id,
            new MintRequest(
                request.Name,
                role,
                ttl,
                request.Replace ?? false,
                CallerSessionId(context.User),
                request.LaunchedBy,
                request.Origin,
                request.Cwd,
                request.ExternalRole,
                request.ClaudeSessionId,
                request.Repo,
                grants,
                OAuthClaim(context.User, OAuthTokenHandler.UserNameClaim),
                OAuthClaim(context.User, OAuthTokenHandler.AuthorizationClaim),
                CallerUser(context.User)),
            cancellationToken);

        if (result.Minted is not { } minted)
        {
            return Error(StatusCodes.Status409Conflict, result.Conflict!);
        }

        return Results.Created(
            $"/api/agent-sessions/{minted.SessionId}",
            new MintSessionResponse(minted.SessionId, minted.Name, Pg(minted.Role), minted.Token, minted.ExpiresAt));
    }

    private static async Task<IResult> PatchAsync(
        Guid id,
        PatchSessionRequest request,
        HttpContext context,
        [FromServices] SessionStore store,
        [FromServices] UserStore users,
        [FromServices] RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClaudeSessionId))
        {
            return Error(StatusCodes.Status400BadRequest, "claudeSessionId is required");
        }

        if (await ManagedAsync(id, context.User, store, users, tenant, cancellationToken) is { } refused)
        {
            return refused;
        }

        await store.SetClaudeSessionAsync(id, request.ClaudeSessionId, Rank(context.User), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        HttpContext context,
        [FromServices] SessionStore store,
        [FromServices] UserStore users,
        [FromServices] RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (await ManagedAsync(id, context.User, store, users, tenant, cancellationToken) is { } refused)
        {
            return refused;
        }

        await store.RevokeAsync(id, Rank(context.User), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>A controller's release. A credential that names a user releases that user's sessions only (<see cref="OwnsAsync"/>).</summary>
    private static async Task<IResult> ReleaseByBearerAsync(
        Guid id,
        HttpContext context,
        [FromServices] SessionStore store,
        [FromServices] UserStore users,
        [FromServices] RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (Rank(context.User) != ActorRole.Controller)
        {
            return Error(StatusCodes.Status403Forbidden, "only a controller may release a session name");
        }

        if (await store.FindAsync(tenant.Id, id, cancellationToken) is { } session
            && !await OwnsAsync(session, context.User, users, tenant.Id, cancellationToken))
        {
            return AnotherUsers(session);
        }

        return await ReleaseAsync(id, new Actor(Pg(ActorRole.Controller), ActorRole.Controller), store, tenant, cancellationToken);
    }

    /// <summary>
    /// doc 11 — frees the name so the next mint creates the session fresh. The row stays, under a
    /// tombstone name, because claims and the journal point at it.
    /// </summary>
    private static async Task<IResult> ReleaseAsync(
        Guid id,
        Actor actor,
        SessionStore store,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var result = await store.ReleaseNameAsync(tenant.Id, id, actor, cancellationToken);

        return result.Outcome switch
        {
            ReleaseOutcome.Released => Results.Ok(new ReleaseSessionResponse(id, result.Name!, result.Tombstone!)),
            ReleaseOutcome.AlreadyReleased => Error(
                StatusCodes.Status409Conflict,
                $"this session was already released; it is kept as \"{result.Name}\" for its history"),
            ReleaseOutcome.Holding => Error(
                StatusCodes.Status409Conflict,
                $"the session \"{result.Name}\" still holds live claims on {result.Held}; it may still be working. "
                + $"Revoking it (DELETE /api/agent-sessions/{id}) releases them, and then its name can be released"),
            _ => Error(StatusCodes.Status404NotFound, $"no agent session with id {id}"),
        };
    }

    /// <summary>
    /// "The same principal, or a controller." A static token has no session, so for a session a
    /// static token minted, the same principal is any static caller of a role at least the one it
    /// granted; for one a minted token minted, it is that minting session. On top, a credential that
    /// names a user changes only that user's sessions (<see cref="OwnsAsync"/>).
    /// </summary>
    private static async Task<IResult?> ManagedAsync(
        Guid id,
        ClaimsPrincipal user,
        SessionStore store,
        UserStore users,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (await store.FindAsync(tenant.Id, id, cancellationToken) is not { } session)
        {
            return Error(StatusCodes.Status404NotFound, $"no agent session with id {id}");
        }

        var caller = Rank(user);
        var allowed = caller == ActorRole.Controller
            || (session.GrantedRole is { } granted
                && Rank(granted) <= Rank(caller)
                && session.MintedBySessionId == CallerSessionId(user));

        if (!allowed)
        {
            return Error(
                StatusCodes.Status403Forbidden,
                $"the session \"{session.Name}\" was not minted by this caller; only its minter or a controller may change it");
        }

        return await OwnsAsync(session, user, users, tenant.Id, cancellationToken) ? null : AnotherUsers(session);
    }

    /// <summary>
    /// A member manages the sessions they minted; an admin of the tenant, or a system administrator, every
    /// session in it. A credential with no user — a static token — keeps the role rule alone.
    /// </summary>
    private static async Task<bool> OwnsAsync(
        MintedSession session,
        ClaimsPrincipal user,
        UserStore users,
        Guid tenant,
        CancellationToken cancellationToken)
        => CallerUser(user) is not { } caller
           || session.UserId == caller
           || await users.ManagesAsync(caller, tenant, cancellationToken);

    private static IResult AnotherUsers(MintedSession session)
        => Error(
            StatusCodes.Status403Forbidden,
            $"the session \"{session.Name}\" is another user's; only they, or an admin of this workspace, may change it");

    private static ActorRole? ParseRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        WorkStatePolicies.Worker => ActorRole.Worker,
        WorkStatePolicies.Orchestrator => ActorRole.Orchestrator,
        WorkStatePolicies.Controller => ActorRole.Controller,
        _ => null,
    };

    private static ActorRole Rank(ClaimsPrincipal user)
        => user.IsInRole(WorkStatePolicies.Controller) ? ActorRole.Controller
            : user.IsInRole(WorkStatePolicies.Orchestrator) ? ActorRole.Orchestrator
            : ActorRole.Worker;

    /// <summary>Worker &lt; orchestrator &lt; controller.</summary>
    private static int Rank(ActorRole role) => role switch
    {
        ActorRole.Controller => 3,
        ActorRole.Orchestrator => 2,
        ActorRole.Worker => 1,
        _ => 0,
    };

    private static Guid? CallerSessionId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirst(CallerSession.SessionIdClaim)?.Value, out var id) ? id : null;

    /// <summary>The user behind an OAuth token, or behind a session a user minted; a static token names none.</summary>
    private static Guid? CallerUser(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirst(CallerSession.UserIdClaim)?.Value, out var id) ? id : null;

    /// <summary>The human's name, or the authorization, behind an OAuth caller; a static or <c>wss_</c> token names neither.</summary>
    private static string? OAuthClaim(ClaimsPrincipal user, string type)
        => user.Identities
            .FirstOrDefault(identity => identity.AuthenticationType == OAuthTokenHandler.SchemeName)?
            .FindFirst(type)?.Value;

    private static string Pg(ActorRole role) => role.ToString().ToLowerInvariant();

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}
