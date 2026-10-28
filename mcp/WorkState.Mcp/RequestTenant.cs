using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace WorkState.Mcp;

/// <summary>
/// The workspace this request acts in, read from the principal and from nothing else (C62): every
/// authentication handler puts <see cref="Claim"/> on the identity it builds, and the policies refuse a
/// principal that has none, so a request path never reaches a default, a header or a query parameter.
/// </summary>
public sealed class RequestTenant(IHttpContextAccessor accessor)
{
    /// <summary>A workspace id. Set by the cookie, a static or <c>wss_</c> token, or an OAuth token.</summary>
    public const string Claim = "workstate:tenant";

    private Guid? _id;

    public Guid Id => _id ??= Of(accessor.HttpContext?.User)
                              ?? throw new InvalidOperationException("The request reached a tenant-scoped path without a tenant.");

    /// <summary>
    /// The principal's one tenant. A request that authenticated with two credentials naming different
    /// workspaces — a cookie beside a bearer token — has none, rather than whichever was read first.
    /// </summary>
    public static Guid? Of(ClaimsPrincipal? user)
    {
        Guid? tenant = null;
        foreach (var claim in user?.FindAll(Claim) ?? [])
        {
            if (!Guid.TryParse(claim.Value, out var id) || (tenant is { } seen && seen != id))
            {
                return null;
            }

            tenant = id;
        }

        return tenant;
    }
}
