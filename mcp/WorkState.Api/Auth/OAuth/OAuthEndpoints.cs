using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using WorkState.Mcp;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// The passthrough half of <see cref="OAuthServer"/>: OpenIddict has validated the request by the time
/// either handler runs, and what is left is who the user is. The signed-in dashboard user, in the tenant
/// their cookie is in: the token names them by id with the controller role and is bound to that one
/// tenant, so <c>/mcp</c> shows what a controller token shows there and nothing of any other.
/// </summary>
public static class OAuthEndpoints
{
    /// <summary>
    /// The authorization property naming the tenant an approval was given in. A remembered approval issues a
    /// token only for that tenant; in any other the user is asked again.
    /// </summary>
    public const string TenantProperty = "workstate_tenant";

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
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations)
    {
        var request = http.GetOpenIddictServerRequest()
                      ?? throw new InvalidOperationException("The OpenIddict request is missing.");

        // The cookie is the gate, and its validation has just re-read its user and tenant from the table. A
        // fresh install with no user yet has no cookie to present.
        var session = await http.AuthenticateAsync(DashboardAuth.SchemeName);
        if (!session.Succeeded
            || !session.Principal.IsInRole(DashboardAuth.Role)
            || !Guid.TryParse(session.Principal.FindFirstValue(DashboardAuth.UserIdClaim), out var user)
            || RequestTenant.Of(session.Principal) is not { } tenant)
        {
            return request.HasPromptValue(PromptValues.None)
                ? Results.Forbid(
                    OAuthError(Errors.LoginRequired, "No one is signed in."),
                    [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme])
                : Results.LocalRedirect($"{ConnectPages.SignInPath}?{ConnectPages.ReturnUrl}={Uri.EscapeDataString(await AuthorizeUrlAsync(http.Request))}");
        }

        var application = await applications.FindByClientIdAsync(request.ClientId ?? string.Empty, http.RequestAborted)
                          ?? throw new InvalidOperationException("OpenIddict admitted a client it cannot find.");
        var applicationId = await applications.GetIdAsync(application, http.RequestAborted);
        var subject = user.ToString();
        var name = session.Principal.Identity?.Name ?? subject;

        // The consent page's answer, ahead of any remembered approval so that Deny always means deny. SameSite
        // keeps it the user's: a POST from another site carries no session cookie and went to sign-in above.
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null;
        var answer = form is not null && form.TryGetValue(ConnectPages.ConsentField, out var field) ? field.ToString() : null;

        if (answer is not null && answer != ConnectPages.Granted)
        {
            return Results.Forbid(
                OAuthError(Errors.AccessDenied, "The user declined the request."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        // An approval is for the tenant the page showed. Switched since, in another tab, the page asks again.
        if (answer is not null
            && form!.TryGetValue(ConnectPages.TenantField, out var shown)
            && (!Guid.TryParse(shown, out var shownTenant) || shownTenant != tenant))
        {
            return Results.LocalRedirect(ConsentUrl(await AuthorizeUrlAsync(http.Request)));
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
                if (IsFor(await authorizations.GetPropertiesAsync(remembered, http.RequestAborted), tenant))
                {
                    return Issue(http, request, subject, name, tenant, await authorizations.GetIdAsync(remembered, http.RequestAborted));
                }
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
            descriptor.Properties[TenantProperty] = JsonSerializer.SerializeToElement(tenant.ToString());

            var authorization = await authorizations.CreateAsync(descriptor, http.RequestAborted);
            return Issue(http, request, subject, name, tenant, await authorizations.GetIdAsync(authorization, http.RequestAborted));
        }

        return request.HasPromptValue(PromptValues.None)
            ? Results.Forbid(
                OAuthError(Errors.ConsentRequired, "This client has not been approved in this workspace."),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme])
            : Results.LocalRedirect(ConsentUrl(await AuthorizeUrlAsync(http.Request)));
    }

    private static bool IsFor(ImmutableDictionary<string, JsonElement> properties, Guid tenant)
        => properties.TryGetValue(TenantProperty, out var value)
           && value.ValueKind == JsonValueKind.String
           && Guid.TryParse(value.GetString(), out var approved)
           && approved == tenant;

    private static string ConsentUrl(string authorizeUrl) => ConnectPages.ConsentPath + authorizeUrl[OAuthServer.AuthorizePath.Length..];

    /// <summary>
    /// Approved: the user's principal in the tenant, tied to the permanent authorization that remembers the
    /// approval, so revoking that authorization ends every token issued under it.
    /// </summary>
    private static IResult Issue(
        HttpContext http,
        OpenIddictRequest request,
        string subject,
        string name,
        Guid tenant,
        string? authorizationId)
    {
        var resources = request.GetResources() is { IsDefaultOrEmpty: false } requested
            ? requested
            : [OAuthServer.Resource(OAuthServer.Origin(http))];

        var principal = Principal(subject, name, tenant, request.GetScopes(), resources);
        principal.SetAuthorizationId(authorizationId);

        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// This request as a same-origin <c>/connect/authorize</c> URL, whether it came as a query or as the
    /// consent page's form, without the consent page's own fields: where the sign-in page sends the user back
    /// to, and the query the consent page is opened with.
    /// </summary>
    private static async Task<string> AuthorizeUrlAsync(HttpRequest request)
    {
        IEnumerable<KeyValuePair<string, StringValues>> fields = request.HasFormContentType
            ? (await request.ReadFormAsync(request.HttpContext.RequestAborted))
                .Where(field => field.Key is not (ConnectPages.ConsentField or ConnectPages.TenantField))
            : request.Query;

        return OAuthServer.AuthorizePath + QueryString.Create(
            fields.SelectMany(field => field.Value.Select(value => KeyValuePair.Create(field.Key, value))));
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http, [FromServices] UserStore users)
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

        // Re-checked on every exchange, refresh included: a membership removed or a user disabled ends every
        // connection at its next refresh, not at the refresh token's expiry.
        var claimed = Guid.TryParse(principal.GetClaim(RequestTenant.Claim), out var id) ? id : (Guid?)null;
        var (grant, refusal) = await users.OAuthUserAsync(principal.GetClaim(Claims.Subject) ?? string.Empty, claimed, http.RequestAborted);
        if (grant is not { User: var user, Tenant: var tenant })
        {
            return Results.Forbid(
                OAuthError(Errors.InvalidGrant, refusal!),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        // The same user and tenant again. A grant issued before tokens named a tenant names its user by name and
        // no tenant; it comes back as one that does, under the authorization it already had, so its client keeps
        // working without a new sign-in and revoking that authorization still ends it.
        principal.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Name, user.Name)
            .SetClaim(RequestTenant.Claim, tenant.ToString());
        principal.SetDestinations(DestinationsOf);

        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    public static ClaimsPrincipal Principal(
        string subject,
        string name,
        Guid tenant,
        ImmutableArray<string> scopes,
        IEnumerable<string> resources)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, subject)
            .SetClaim(Claims.Name, name)
            .SetClaim(RequestTenant.Claim, tenant.ToString())
            .SetClaims(Claims.Role, [WorkStatePolicies.Controller]);

        identity.SetScopes(scopes);
        identity.SetResources(resources);
        identity.SetDestinations(DestinationsOf);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>Every claim goes into the access token, and the tenant with it; the name into the identity token too.</summary>
    private static IEnumerable<string> DestinationsOf(Claim claim) => claim.Type switch
    {
        Claims.Name => [Destinations.AccessToken, Destinations.IdentityToken],
        _ => [Destinations.AccessToken],
    };

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
        ClientIcons.Describe(descriptor, body?.LogoUri, body?.ClientUri);
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
                [ResponseTypes.Code],
                ClientIcons.Declared(descriptor, ClientIcons.LogoUri),
                ClientIcons.Declared(descriptor, ClientIcons.ClientUri)),
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
                Permissions.Prefixes.Scope + OAuthServer.ReadScope,
                Permissions.Prefixes.Scope + OAuthServer.SessionsScope,
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
        [property: JsonPropertyName("redirect_uris")] List<string>? RedirectUris,
        [property: JsonPropertyName("logo_uri")] string? LogoUri = null,
        [property: JsonPropertyName("client_uri")] string? ClientUri = null);

    private sealed record RegistrationResponse(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
        [property: JsonPropertyName("client_name")] string ClientName,
        [property: JsonPropertyName("redirect_uris")] string[] RedirectUris,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
        [property: JsonPropertyName("grant_types")] string[] GrantTypes,
        [property: JsonPropertyName("response_types")] string[] ResponseTypes,
        [property: JsonPropertyName("logo_uri"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LogoUri,
        [property: JsonPropertyName("client_uri"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientUri);

    private sealed record RegistrationErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string ErrorDescription);
}
