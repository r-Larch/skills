using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
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
    RepoArg? Repo = null);

public sealed record MintSessionResponse(Guid SessionId, string Name, string Role, string Token, DateTimeOffset ExpiresAt);

public sealed record PatchSessionRequest(string? ClaudeSessionId);

/// <summary>
/// doc 14 §2 — per-session tokens. A write surface for launchers, so it sits beside the App API and
/// not under <c>/api/v1</c>, which is read-only by rule. Every call needs an orchestrator or
/// controller bearer; a worker is forbidden and the dashboard cookie is not a bearer at all.
/// </summary>
public static class AgentSessionEndpoints
{
    public static void MapAgentSessions(this RouteGroupBuilder api)
    {
        var sessions = api.MapGroup("/agent-sessions").RequireAuthorization(WorkStatePolicies.Orchestrator);

        sessions.MapPost("", MintAsync);
        sessions.MapPatch("/{id:guid}", PatchAsync);
        sessions.MapDelete("/{id:guid}", RevokeAsync);
    }

    private static async Task<IResult> MintAsync(
        MintSessionRequest request,
        HttpContext context,
        [FromServices] SessionStore store,
        [FromServices] BoardQueries board,
        [FromServices] WorkspaceOptions workspace,
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

        if (await board.WorkspaceAsync(workspace.Slug, cancellationToken) is not { } workspaceId)
        {
            return Error(StatusCodes.Status409Conflict, "no workspace exists yet to mint a session into");
        }

        var result = await store.MintAsync(
            workspaceId,
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
                request.Repo),
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
        [FromServices] BoardQueries board,
        [FromServices] WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClaudeSessionId))
        {
            return Error(StatusCodes.Status400BadRequest, "claudeSessionId is required");
        }

        if (await ManagedAsync(id, context.User, store, board, workspace, cancellationToken) is { } refused)
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
        [FromServices] BoardQueries board,
        [FromServices] WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (await ManagedAsync(id, context.User, store, board, workspace, cancellationToken) is { } refused)
        {
            return refused;
        }

        await store.RevokeAsync(id, Rank(context.User), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// "The same principal, or a controller." A static token has no session, so for a session a
    /// static token minted, the same principal is any static caller of a role at least the one it
    /// granted; for one a minted token minted, it is that minting session.
    /// </summary>
    private static async Task<IResult?> ManagedAsync(
        Guid id,
        ClaimsPrincipal user,
        SessionStore store,
        BoardQueries board,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        var session = await board.WorkspaceAsync(workspace.Slug, cancellationToken) is { } workspaceId
            ? await store.FindAsync(workspaceId, id, cancellationToken)
            : null;

        if (session is null)
        {
            return Error(StatusCodes.Status404NotFound, $"no agent session with id {id}");
        }

        var caller = Rank(user);
        var allowed = caller == ActorRole.Controller
            || (session.GrantedRole is { } granted
                && Rank(granted) <= Rank(caller)
                && session.MintedBySessionId == CallerSessionId(user));

        return allowed
            ? null
            : Error(
                StatusCodes.Status403Forbidden,
                $"the session \"{session.Name}\" was not minted by this caller; only its minter or a controller may change it");
    }

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

    private static string Pg(ActorRole role) => role.ToString().ToLowerInvariant();

    private static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}
