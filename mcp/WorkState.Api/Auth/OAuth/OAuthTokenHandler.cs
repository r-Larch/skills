using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using WorkState.Core.Writing;
using WorkState.Mcp;
using ActorRole = WorkState.Core.Domain.ActorRole;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// <c>/mcp</c>'s third credential, beside <see cref="StaticTokenHandler"/>'s two, and the one that answers
/// an anonymous request with RFC 9728's challenge. The read API and the agent-session group take it too,
/// with <see cref="OAuthScopeRequirement"/> on top.
/// <para>
/// It touches only a JWT-shaped bearer — dotted, and not a <c>wss_</c> session token — so a static or
/// minted token is judged by the handler that always judged it and nothing else (C31). An access token
/// OpenIddict accepts becomes the same principal a static controller token produces, so
/// <see cref="CallerIdentity"/>, <see cref="CallerSession"/> and the tool policies see no difference.
/// </para>
/// </summary>
public sealed class OAuthTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DashboardOptions dashboard,
    IAuthenticationSchemeProvider schemes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkStateOAuth";

    /// <summary>One claim per granted scope, read by <see cref="OAuthScopeRequirement"/>.</summary>
    public const string ScopeClaim = "workstate_scope";

    /// <summary>The OpenIddict authorization the token was issued under; a mint records it (doc 14 §2).</summary>
    public const string AuthorizationClaim = "workstate_authorization";

    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Presented() is not { } token || !IsJwt(token) || !await ServesOAuthAsync())
        {
            return AuthenticateResult.NoResult();
        }

        var validated = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (validated is not { Succeeded: true, Principal: { } principal })
        {
            return AuthenticateResult.Fail(validated.Failure ?? new InvalidOperationException("Unrecognised bearer token."));
        }

        // The token outlives a change of operator otherwise; the token endpoint makes the same check.
        if (!OAuthEndpoints.IsOperator(principal, dashboard)
            || !principal.HasClaim(OpenIddictConstants.Claims.Role, WorkStatePolicies.Controller))
        {
            return AuthenticateResult.Fail("The token's user is no longer the operator.");
        }

        var subject = principal.GetClaim(OpenIddictConstants.Claims.Subject)!;
        var identity = StaticTokenHandler.Identity(StaticTokenHandler.Nested(ActorRole.Controller), SchemeName);
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, subject));
        await TenantAsync(identity, subject, principal);
        foreach (var scope in principal.GetScopes())
        {
            identity.AddClaim(new Claim(ScopeClaim, scope));
        }

        if (principal.GetAuthorizationId() is { Length: > 0 } authorization)
        {
            identity.AddClaim(new Claim(AuthorizationClaim, authorization));
        }

        if (Request.Headers[CallerSession.ClientHeader].ToString().Trim() is { Length: > 0 } key)
        {
            identity.AddClaim(new Claim(CallerSession.ClientKeyClaim, key));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>
    /// The token's <see cref="RequestTenant.Claim"/> once user-oauth issues one, else its user's only
    /// membership. A token that yields neither still authenticates, carrying the reason instead, and the
    /// tenant requirement turns that into a 403 naming what to do (<see cref="TenantRequirement"/>).
    /// </summary>
    private async Task TenantAsync(ClaimsIdentity identity, string subject, ClaimsPrincipal token)
    {
        var claimed = Guid.TryParse(token.GetClaim(RequestTenant.Claim), out var id) ? id : (Guid?)null;
        var (tenant, refusal) = Context.RequestServices.GetService<UserStore>() is { } users
            ? await users.OAuthTenantAsync(subject, claimed, Context.RequestAborted)
            : (null, "This server has no user store to find the token's workspace in.");

        identity.AddClaim(tenant is { } found
            ? new Claim(RequestTenant.Claim, found.ToString())
            : new Claim(TenantRequirement.RefusalClaim, refusal!));
    }

    /// <summary>
    /// The resource-metadata challenge for a request with no credential or with a JWT. A request that
    /// presented a static or <c>wss_</c> token gets the bare 401 it always got. Under <c>/api</c> the
    /// metadata is <c>/api</c>'s; the MCP handler would name one per request path.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if ((Presented() is not { } token || IsJwt(token)) && await ServesOAuthAsync())
        {
            if (!Request.Path.StartsWithSegments(OAuthServer.ApiPath))
            {
                await Context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme, properties);
                return;
            }

            var metadata = OAuthServer.Origin(Context) + OAuthServer.ResourceMetadataPath + OAuthServer.ApiPath;
            Response.Headers.Append(HeaderNames.WWWAuthenticate, $"Bearer resource_metadata=\"{metadata}\"");
        }

        await base.HandleChallengeAsync(properties);
    }

    private string? Presented()
    {
        var raw = Request.Headers[HeaderNames.Authorization].ToString();
        return raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? raw[BearerPrefix.Length..].Trim()
            : null;
    }

    private static bool IsJwt(string token)
        => token.Contains('.') && !token.StartsWith(SessionToken.Prefix, StringComparison.Ordinal);

    /// <summary>A host without a store has no OAuth server, and so nothing to validate or advertise.</summary>
    private async Task<bool> ServesOAuthAsync()
        => await schemes.GetSchemeAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) is not null;
}
