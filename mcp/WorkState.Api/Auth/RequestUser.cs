using System.Security.Claims;
using WorkState.Core.Data;
using WorkState.Mcp;

namespace WorkState.Api.Auth;

/// <summary>
/// The user a request acts for, read from the principal and from nothing else, as <see cref="RequestTenant"/> reads
/// its tenant: the cookie's, an OAuth token's or a user-minted <c>wss_</c> token's. It decides which projects the
/// request's tenant connections see (doc 02 §15b); a static token has no user and sees every project of its tenant.
/// </summary>
internal static class RequestUser
{
    /// <summary>
    /// False when the principal names two different users — a cookie beside another user's bearer token — or one that
    /// is not an id: such a request acts for no one rather than whichever was read first.
    /// </summary>
    public static bool TryOf(ClaimsPrincipal? principal, out Guid? user)
    {
        user = null;
        foreach (var claim in principal?.Claims ?? [])
        {
            if (claim.Type is not (CallerSession.UserIdClaim or DashboardAuth.UserIdClaim))
            {
                continue;
            }

            if (!Guid.TryParse(claim.Value, out var id) || (user is { } seen && seen != id))
            {
                user = null;
                return false;
            }

            user = id;
        }

        return true;
    }

    /// <summary>The request's tenant scope: its tenant and its user, or no tenant when the user is in doubt.</summary>
    public static IDisposable Enter(ClaimsPrincipal? principal)
        => TryOf(principal, out var user)
            ? TenantScope.EnterAs(RequestTenant.Of(principal), user)
            : TenantScope.EnterAs(null, null);
}
