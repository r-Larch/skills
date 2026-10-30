using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using WorkState.Core.Domain;
using WorkState.Core.Writing;
using WorkState.Mcp;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// One app that holds the signed-in user's access in this tenant: a permanent authorization from the consent page.
/// <c>LastUsedAt</c> is when the newest token under it was issued — a sign-in or a refresh — or null.
/// </summary>
public sealed record ConnectedAppView(
    string Id,
    string ClientId,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastUsedAt,
    int Sessions);

public sealed record ConnectedAppsView(DateTimeOffset AsOf, IReadOnlyList<ConnectedAppView> Apps);

public sealed record RevokedAppView(long TokensRevoked, int SessionsRevoked);

/// <summary>
/// doc 13, the account page's connected apps: <c>/api/session/apps</c>, by cookie. Only the cookie's user's own
/// authorizations in the cookie's tenant; any other id is not found, never forbidden, and changes nothing.
/// </summary>
public static class ConnectedApps
{
    public static async Task<IResult> ListAsync(
        HttpContext context,
        [FromServices] IOpenIddictAuthorizationManager authorizations,
        [FromServices] IOpenIddictApplicationManager applications,
        [FromServices] IOpenIddictTokenManager tokens,
        [FromServices] SessionStore sessions)
    {
        var cancellationToken = context.RequestAborted;
        var (user, tenant) = Caller(context.User);
        var owned = await OwnedAsync(authorizations, user, tenant, cancellationToken);
        var live = (await sessions.LiveMintedUnderAsync(tenant, owned.Keys, cancellationToken))
            .CountBy(session => session.AuthorizationId)
            .ToDictionary();

        var apps = new List<ConnectedAppView>(owned.Count);
        foreach (var (id, authorization) in owned)
        {
            var application = await authorizations.GetApplicationIdAsync(authorization, cancellationToken) is { } applicationId
                ? await applications.FindByIdAsync(applicationId, cancellationToken)
                : null;
            var clientId = application is null ? null : await applications.GetClientIdAsync(application, cancellationToken);

            DateTimeOffset? lastUsed = null;
            await foreach (var token in tokens.FindByAuthorizationIdAsync(id, cancellationToken))
            {
                if (await tokens.GetCreationDateAsync(token, cancellationToken) is { } issued && (lastUsed is null || issued > lastUsed))
                {
                    lastUsed = issued;
                }
            }

            apps.Add(new ConnectedAppView(
                id,
                clientId ?? string.Empty,
                (application is null ? null : await applications.GetDisplayNameAsync(application, cancellationToken)) ?? clientId ?? id,
                await authorizations.GetScopesAsync(authorization, cancellationToken),
                await authorizations.GetCreationDateAsync(authorization, cancellationToken),
                lastUsed,
                live.GetValueOrDefault(id)));
        }

        return Results.Ok(new ConnectedAppsView(
            DateTimeOffset.UtcNow,
            [.. apps.OrderByDescending(app => app.LastUsedAt ?? app.CreatedAt).ThenByDescending(app => app.CreatedAt)]));
    }

    /// <summary>
    /// The authorization and every token under it, then each live session minted with one of them, as its user revoking
    /// it — which releases its claims and journals them.
    /// </summary>
    public static async Task<IResult> RevokeAsync(
        string id,
        HttpContext context,
        [FromServices] IOpenIddictAuthorizationManager authorizations,
        [FromServices] IOpenIddictTokenManager tokens,
        [FromServices] SessionStore sessions)
    {
        var cancellationToken = context.RequestAborted;
        var (user, tenant) = Caller(context.User);
        if (!(await OwnedAsync(authorizations, user, tenant, cancellationToken)).ContainsKey(id)
            || await OAuthServer.RevokeAuthorizationAsync(authorizations, tokens, id, cancellationToken) is not { } revokedTokens)
        {
            return Results.Json(new { error = $"no connected app with id {id}" }, statusCode: StatusCodes.Status404NotFound);
        }

        var actor = new Actor(context.User.Identity?.Name ?? user.ToString(), ActorRole.Human);
        var minted = await sessions.LiveMintedUnderAsync(tenant, [id], cancellationToken);
        foreach (var session in minted)
        {
            await sessions.RevokeAsync(session.SessionId, actor, cancellationToken);
        }

        return Results.Ok(new RevokedAppView(revokedTokens, minted.Count));
    }

    /// <summary>The user's valid permanent authorizations whose tenant is <paramref name="tenant"/>, by id.</summary>
    private static async Task<Dictionary<string, object>> OwnedAsync(
        IOpenIddictAuthorizationManager authorizations,
        Guid user,
        Guid tenant,
        CancellationToken cancellationToken)
    {
        var owned = new Dictionary<string, object>(StringComparer.Ordinal);
        await foreach (var authorization in authorizations.FindBySubjectAsync(user.ToString(), cancellationToken))
        {
            if (await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken)
                && await authorizations.HasTypeAsync(authorization, AuthorizationTypes.Permanent, cancellationToken)
                && OAuthEndpoints.IsFor(await authorizations.GetPropertiesAsync(authorization, cancellationToken), tenant)
                && await authorizations.GetIdAsync(authorization, cancellationToken) is { } id)
            {
                owned[id] = authorization;
            }
        }

        return owned;
    }

    /// <summary>The cookie's user and tenant, as <see cref="DashboardAuth"/> re-read them on this request (C62).</summary>
    private static (Guid User, Guid Tenant) Caller(ClaimsPrincipal principal)
        => (Guid.Parse(principal.FindFirstValue(DashboardAuth.UserIdClaim)!), RequestTenant.Of(principal)!.Value);
}
