using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using WorkState.Mcp;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// The passthrough half of <see cref="OAuthServer"/>: OpenIddict has validated the request by the time
/// either handler runs, and what is left is who the user is. There is one — the dashboard's operator —
/// and the token names them with the controller role, so <c>/mcp</c> shows what a controller token shows.
/// </summary>
public static class OAuthEndpoints
{
    private const int MaxRedirectUris = 10;
    private const int MaxClientNameLength = 200;

    public static void MapOAuth(this WebApplication app)
    {
        app.MapMethods(OAuthServer.AuthorizePath, [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).AllowAnonymous();
        app.MapPost(OAuthServer.TokenPath, ExchangeAsync).AllowAnonymous();
        app.MapPost(OAuthServer.RegisterPath, RegisterAsync).AllowAnonymous().RequireRateLimiting(RateLimits.Registration);
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        DashboardOptions dashboard,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations)
    {
        var request = http.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenIddict request is missing.");

        if (!dashboard.Configured)
        {
            return Results.Forbid(
                OAuthError(Errors.AccessDenied, "The dashboard has no operator configured, so no one can sign in."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var session = await http.AuthenticateAsync(DashboardAuth.SchemeName);
        if (!session.Succeeded || !session.Principal.IsInRole(DashboardAuth.Role))
        {
            return request.HasPromptValue(PromptValues.None)
                ? Results.Forbid(
                    OAuthError(Errors.LoginRequired, "The operator is not signed in."),
                    [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme])
                : Results.LocalRedirect($"{ConnectPages.SignInPath}?{ConnectPages.ReturnUrl}={Uri.EscapeDataString(await AuthorizeUrlAsync(http.Request))}");
        }

        var application = await applications.FindByClientIdAsync(request.ClientId ?? string.Empty, http.RequestAborted)
                          ?? throw new InvalidOperationException("OpenIddict admitted a client it cannot find.");
        var applicationId = await applications.GetIdAsync(application, http.RequestAborted);
        var subject = dashboard.Username!;

        // The consent page's answer, ahead of any remembered approval so that Deny always means deny. SameSite
        // keeps it the operator's: a POST from another site carries no session cookie and went to sign-in above.
        var answer = http.Request.HasFormContentType
                     && (await http.Request.ReadFormAsync(http.RequestAborted)).TryGetValue(ConnectPages.ConsentField, out var field)
            ? field.ToString()
            : null;

        if (answer is not null && answer != ConnectPages.Granted)
        {
            return Results.Forbid(
                OAuthError(Errors.AccessDenied, "The operator declined the request."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        if (answer is not null || !request.HasPromptValue(PromptValues.Consent))
        {
            await foreach (var remembered in authorizations.FindAsync(
                               subject,
                               applicationId!,
                               Statuses.Valid,
                               AuthorizationTypes.Permanent,
                               request.GetScopes(),
                               http.RequestAborted))
            {
                return Issue(http, request, subject, await authorizations.GetIdAsync(remembered, http.RequestAborted));
            }
        }

        if (answer is not null)
        {
            var descriptor = new OpenIddictAuthorizationDescriptor
            {
                ApplicationId = applicationId,
                Subject = subject,
                Type = AuthorizationTypes.Permanent,
                Status = Statuses.Valid,
                CreationDate = DateTimeOffset.UtcNow,
            };
            descriptor.Scopes.UnionWith(request.GetScopes());

            var authorization = await authorizations.CreateAsync(descriptor, http.RequestAborted);
            return Issue(http, request, subject, await authorizations.GetIdAsync(authorization, http.RequestAborted));
        }

        return request.HasPromptValue(PromptValues.None)
            ? Results.Forbid(
                OAuthError(Errors.ConsentRequired, "This client has not been approved."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme])
            : Results.LocalRedirect(ConnectPages.ConsentPath + (await AuthorizeUrlAsync(http.Request))[OAuthServer.AuthorizePath.Length..]);
    }

    /// <summary>
    /// Approved: the operator's principal, tied to the permanent authorization that remembers the approval,
    /// so revoking that authorization ends every token issued under it.
    /// </summary>
    private static IResult Issue(HttpContext http, OpenIddictRequest request, string subject, string? authorizationId)
    {
        var resources = request.GetResources() is { IsDefaultOrEmpty: false } requested
            ? requested
            : [OAuthServer.Resource(OAuthServer.Origin(http))];

        var principal = Principal(subject, request.GetScopes(), resources);
        principal.SetAuthorizationId(authorizationId);

        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// This request as a same-origin <c>/connect/authorize</c> URL, whether it came as a query or as the
    /// consent page's form, without the consent answer: where the sign-in page sends the operator back to,
    /// and the query the consent page is opened with.
    /// </summary>
    private static async Task<string> AuthorizeUrlAsync(HttpRequest request)
    {
        IEnumerable<KeyValuePair<string, StringValues>> fields = request.HasFormContentType
            ? (await request.ReadFormAsync(request.HttpContext.RequestAborted))
                .Where(field => field.Key != ConnectPages.ConsentField)
            : request.Query;

        return OAuthServer.AuthorizePath + QueryString.Create(
            fields.SelectMany(field => field.Value.Select(value => KeyValuePair.Create(field.Key, value))));
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http, DashboardOptions dashboard)
    {
        var request = http.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenIddict request is missing.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Results.Forbid(
                OAuthError(Errors.UnsupportedGrantType, "Only authorization_code and refresh_token are supported."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var redeemed = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (redeemed.Principal is not { } principal)
        {
            return Results.Forbid(
                OAuthError(Errors.InvalidGrant, "The grant is no longer valid."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        // Re-checked on every exchange, refresh included: renaming the operator or switching the dashboard
        // off ends every connection at its next refresh, not at the refresh token's expiry.
        if (!IsOperator(principal, dashboard))
        {
            return Results.Forbid(
                OAuthError(Errors.InvalidGrant, "The user this grant was issued to is no longer the operator."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    public static bool IsOperator(ClaimsPrincipal principal, DashboardOptions dashboard)
        => dashboard.Configured
           && string.Equals(principal.GetClaim(Claims.Subject), dashboard.Username, StringComparison.Ordinal);

    public static ClaimsPrincipal Principal(string username, ImmutableArray<string> scopes, IEnumerable<string> resources)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, username)
            .SetClaim(Claims.Name, username)
            .SetClaims(Claims.Role, [WorkStatePolicies.Controller]);

        identity.SetScopes(scopes);
        identity.SetResources(resources);

        identity.SetDestinations(claim => claim.Type switch
        {
            Claims.Name => [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken],
        });

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// RFC 7591, public clients only. Anonymous by nature — the client has no credential yet — so it
    /// accepts only what a public client can use: loopback http or https callbacks, a bounded number of
    /// them, and no secret.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        RegistrationRequest? body,
        IOpenIddictApplicationManager applications,
        CancellationToken cancellationToken)
    {
        var requested = body?.RedirectUris ?? [];
        if (requested.Count is 0 or > MaxRedirectUris)
        {
            return RegistrationError("invalid_redirect_uri", $"Between 1 and {MaxRedirectUris} redirect_uris are required.");
        }

        var callbacks = new List<Uri>();
        foreach (var value in requested)
        {
            if (!IsAcceptableRedirect(value, out var uri))
            {
                return RegistrationError(
                    "invalid_redirect_uri",
                    $"'{value}' is not an https URI or an http URI on a loopback host.");
            }

            callbacks.Add(uri);
        }

        if (body?.ClientName is { Length: > MaxClientNameLength })
        {
            return RegistrationError("invalid_client_metadata", $"client_name is longer than {MaxClientNameLength} characters.");
        }

        var clientId = Guid.NewGuid().ToString("N");
        var descriptor = PublicClient(clientId, string.IsNullOrWhiteSpace(body?.ClientName) ? "MCP client" : body.ClientName);
        foreach (var callback in callbacks)
        {
            descriptor.RedirectUris.Add(callback);
        }

        await applications.CreateAsync(descriptor, cancellationToken);

        return Results.Json(
            new RegistrationResponse(
                clientId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                descriptor.DisplayName!,
                [.. requested],
                ClientAuthenticationMethods.None,
                [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken],
                [ResponseTypes.Code]),
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>What every client here may do, whether it registered or named a metadata document.</summary>
    public static OpenIddictApplicationDescriptor PublicClient(string clientId, string displayName)
        => new()
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + OAuthServer.McpScope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

    public static bool IsAcceptableRedirect(string? value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }

    private static AuthenticationProperties OAuthError(string error, string description)
        => new(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        });

    private static IResult RegistrationError(string error, string description)
        => Results.Json(new RegistrationErrorResponse(error, description), statusCode: StatusCodes.Status400BadRequest);

    public sealed record RegistrationRequest(
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] List<string>? RedirectUris);

    private sealed record RegistrationResponse(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
        [property: JsonPropertyName("client_name")] string ClientName,
        [property: JsonPropertyName("redirect_uris")] string[] RedirectUris,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
        [property: JsonPropertyName("grant_types")] string[] GrantTypes,
        [property: JsonPropertyName("response_types")] string[] ResponseTypes);

    private sealed record RegistrationErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string ErrorDescription);
}
