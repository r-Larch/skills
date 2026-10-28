using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using WorkState.Mcp;

namespace WorkState.Api.Auth;

/// <summary>
/// C62: a principal acts in its tenant or not at all. Every endpoint policy carries this, so no request path
/// reaches <see cref="RequestTenant"/> without one, and none falls back to a default.
/// </summary>
public sealed class TenantRequirement : AuthorizationHandler<TenantRequirement>, IAuthorizationRequirement
{
    /// <summary>Why a handler that authenticated the credential could not name its tenant.</summary>
    public const string RefusalClaim = "workstate:tenant-refusal";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRequirement requirement)
    {
        if (RequestTenant.Of(context.User) is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    public static string Refusal(ClaimsPrincipal user)
        => user.FindFirst(RefusalClaim)?.Value
           ?? (user.HasClaim(c => c.Type == RequestTenant.Claim)
               ? "This request carries credentials for two different workspaces. Send one."
               : "This credential names no workspace. Sign in again.");
}

/// <summary>
/// The 403 of a principal with no tenant says what to do, which the schemes' own forbid cannot: the cookie's
/// is a bare status and a bearer's is a header. Every other outcome is the default's.
/// </summary>
public sealed class TenantRefusalHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<TenantRequirement>().Any() == true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = TenantRequirement.Refusal(context.User) });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
