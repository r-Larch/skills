using Microsoft.AspNetCore.Authorization;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// A scope an OAuth principal must hold on a surface that also admits static tokens, cookies or <c>wss_</c>
/// tokens. Only the identity <see cref="OAuthTokenHandler"/> built is judged: the others carry no scopes and
/// never needed one, so they pass exactly as before (C31).
/// </summary>
public sealed class OAuthScopeRequirement(string scope)
    : AuthorizationHandler<OAuthScopeRequirement>, IAuthorizationRequirement
{
    public string Scope { get; } = scope;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OAuthScopeRequirement requirement)
    {
        if (context.User.Identities
            .Where(identity => identity.AuthenticationType == OAuthTokenHandler.SchemeName)
            .All(identity => identity.HasClaim(OAuthTokenHandler.ScopeClaim, requirement.Scope)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
