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
/// an anonymous request with RFC 9728's challenge.
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

        var identity = StaticTokenHandler.Identity(StaticTokenHandler.Nested(ActorRole.Controller), SchemeName);
        identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, principal.GetClaim(OpenIddictConstants.Claims.Subject)!));
        if (Request.Headers[CallerSession.ClientHeader].ToString().Trim() is { Length: > 0 } key)
        {
            identity.AddClaim(new Claim(CallerSession.ClientKeyClaim, key));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>
    /// The resource-metadata challenge for a request with no credential or with a JWT. A request that
    /// presented a static or <c>wss_</c> token gets the bare 401 it always got.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if ((Presented() is not { } token || IsJwt(token)) && await ServesOAuthAsync())
        {
            await Context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme, properties);
            return;
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
