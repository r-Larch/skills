using Microsoft.AspNetCore;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation;
using WorkState.Core.Data;
using WorkState.Core.Domain;
using WorkState.Mcp;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// The OAuth 2.1 authorization server <c>/mcp</c> and <c>/api</c> trust beside the static and minted tokens: OpenIddict
/// in passthrough mode over the EF stores in <see cref="WorkStateDbContext"/>, authorization code with
/// PKCE S256 and refresh only.
/// <para>
/// Two values are per request rather than per process, because a local install has no public URL and
/// is reached on whatever loopback origin the client used: the issuer (OpenIddict infers it when none
/// is set) and the resources, <c>&lt;origin&gt;/mcp</c> and <c>&lt;origin&gt;/api</c>. Both come from
/// <see cref="WebBase"/>, so the authorize request, the token's audience and the check on each surface
/// name the same origin.
/// </para>
/// <para>
/// The two resources are two audiences (RFC 8707): a token is for one surface, and the other refuses it.
/// </para>
/// </summary>
public static class OAuthServer
{
    public const string AuthorizePath = "/connect/authorize";
    public const string TokenPath = "/connect/token";
    public const string RevokePath = "/connect/revoke";
    public const string RegisterPath = "/connect/register";

    public const string McpScope = "mcp";
    public const string McpPath = "/mcp";

    /// <summary>GET <c>/api/v1/*</c> and <c>/api/stream</c>.</summary>
    public const string ReadScope = "workstate.read";

    /// <summary>The <c>/api/agent-sessions</c> group: mint, rename and revoke.</summary>
    public const string SessionsScope = "workstate.sessions";

    public const string ApiPath = "/api";

    public const string ResourceMetadataPath = "/.well-known/oauth-protected-resource";

    public const string ClientIdMetadataSupported = "client_id_metadata_document_supported";

    /// <summary>
    /// Set explicitly: left null, a refresh token inherits the lifetime of the principal it was redeemed
    /// from, which on the first exchange is the five-minute authorization code (Nomos found this live).
    /// </summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    public static string Resource(string origin) => origin + McpPath;

    public static string ApiResource(string origin) => origin + ApiPath;

    /// <summary>The resource a bearer on this request must be for: <c>/api</c> under <c>/api</c>, <c>/mcp</c> elsewhere.</summary>
    public static string ResourceFor(HttpContext context)
        => context.Request.Path.StartsWithSegments(ApiPath) ? ApiResource(Origin(context)) : Resource(Origin(context));

    public static void Add(IServiceCollection services, string connectionString, string? publicUrl)
    {
        services.AddDbContext<WorkStateDbContext>(options => options.UseWorkState(connectionString));
        services.AddSingleton<OAuthKeyRing>();

        services.AddOpenIddict()
            .AddCore(core =>
            {
                core.UseEntityFrameworkCore().UseDbContext<WorkStateDbContext>();
                core.ReplaceApplicationManager<OpenIddictEntityFrameworkCoreApplication, WorkStateApplicationManager>();
            })
            .AddServer(server =>
            {
                if (Uri.TryCreate(publicUrl?.Trim(), UriKind.Absolute, out var issuer))
                {
                    server.SetIssuer(issuer);
                }

                server.SetAuthorizationEndpointUris(AuthorizePath)
                    .SetTokenEndpointUris(TokenPath)
                    .SetRevocationEndpointUris(RevokePath);

                server.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange()
                    .SetRefreshTokenLifetime(RefreshTokenLifetime);

                // S256 only: OpenIddict enables plain beside it by default, and a client offered plain will
                // take it. With plain gone, a request naming it or naming no method is refused.
                server.Configure(options => options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

                server.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.OfflineAccess, McpScope, ReadScope, SessionsScope);

                // RegisterResources takes a fixed list and the resource here follows the request's origin;
                // RequireKnownResource below is the same check against that origin.
                server.DisableResourceValidation();
                server.IgnoreResourcePermissions();

                // Likewise keyed on the origin, which is a URL (C32), never on the environment's name.
                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .DisableTransportSecurityRequirement();

                server.AddEventHandler(RequireTransportSecurity);
                server.AddEventHandler(RequireKnownResourceOnAuthorize);
                server.AddEventHandler(RequireKnownResourceOnToken);
                server.AddEventHandler(AdvertiseRegistration);
                server.AddEventHandler(RevokeAuthorizationWithRefreshToken);
            })
            .AddValidation(validation =>
            {
                // In-process: no discovery fetch over the public URL, which from inside a container would
                // hair-pin through the proxy.
                validation.UseLocalServer();
                validation.UseAspNetCore();

                // One user, one database: a lookup per call is what makes a revoked token stop working
                // now rather than when it expires.
                validation.EnableTokenEntryValidation();
                validation.EnableAuthorizationEntryValidation();

                validation.AddEventHandler(RequireSurfaceAudience);
            });

        // Here rather than through AddSigningKey, which wants the key at registration time; the ring
        // needs the database and the DataProtection keys, which only exist once the container does.
        services.AddOptions<OpenIddictServerOptions>().Configure<OAuthKeyRing>((options, ring) =>
        {
            options.SigningCredentials.Add(new SigningCredentials(
                ring.Load(OAuthKey.Signing), SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                ring.Load(OAuthKey.Encryption), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        });

        services.AddAuthentication().AddMcp(options =>
        {
            // Its default forwards authentication to "Bearer". This scheme only challenges; it admits no one.
            options.ForwardAuthenticate = null;
            // The handler answers every path under the well-known prefix; /api is the one that is not /mcp's.
            options.Events.OnResourceMetadataRequest = context =>
            {
                var origin = Origin(context.HttpContext);
                var api = context.HttpContext.Request.Path.StartsWithSegments(ResourceMetadataPath + ApiPath, out var rest)
                          && !rest.HasValue;
                context.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = api ? ApiResource(origin) : Resource(origin),
                    AuthorizationServers = [origin],
                    ScopesSupported = api ? [ReadScope, SessionsScope, Scopes.OfflineAccess] : [McpScope, Scopes.OfflineAccess],
                    BearerMethodsSupported = ["header"],
                };
                return Task.CompletedTask;
            };
        });

        ClientIdMetadata.Add(services);
        services.AddHostedService<OAuthPruning>();
    }

    /// <summary>The scheme and host the request names, or the configured public URL — <see cref="WebBase"/>.</summary>
    public static string Origin(HttpContext context)
        => context.RequestServices.GetRequiredService<WebBase>().Current
           ?? throw new InvalidOperationException("The request has no host to derive the issuer from.");

    /// <summary>
    /// HTTPS, except where the origin is plain HTTP on a loopback host — a local install, which never has
    /// TLS and never leaves the machine. A public URL on https makes every plain request a refusal, even
    /// one that reached the container directly.
    /// </summary>
    private static readonly OpenIddictServerHandlerDescriptor RequireTransportSecurity =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessRequestContext>()
            .UseInlineHandler(context =>
            {
                var request = context.Transaction.GetHttpRequest()
                              ?? throw new InvalidOperationException("No HTTP request on the OpenIddict transaction.");

                // Unknown is every request that is not an OAuth endpoint, /mcp and the dashboard included.
                if (context.EndpointType == OpenIddictServerEndpointType.Unknown
                    || request.IsHttps
                    || IsLoopbackHttp(Origin(request.HttpContext)))
                {
                    return default;
                }

                context.Reject(
                    error: Errors.InvalidRequest,
                    description: "This server only accepts HTTPS requests.");
                return default;
            })
            .SetOrder(OpenIddictServerAspNetCoreHandlers.ValidateTransportSecurityRequirement.Descriptor.Order)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private static readonly OpenIddictServerHandlerDescriptor RequireKnownResourceOnAuthorize =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ValidateAuthorizationRequestContext>()
            .UseInlineHandler(context =>
            {
                if (UnknownResource(context.Transaction, context.Request) is { } unknown)
                {
                    context.Reject(Errors.InvalidTarget, $"The resource '{unknown}' is not served here.");
                }
                else if (BothSurfaces(context.Transaction, context.Request))
                {
                    context.Reject(Errors.InvalidTarget, "A token is for /mcp or for /api, not both; ask for one of them.");
                }

                return default;
            })
            .SetOrder(OpenIddictServerHandlers.Authentication.ValidateResources.Descriptor.Order)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    private static readonly OpenIddictServerHandlerDescriptor RequireKnownResourceOnToken =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ValidateTokenRequestContext>()
            .UseInlineHandler(context =>
            {
                if (UnknownResource(context.Transaction, context.Request) is { } unknown)
                {
                    context.Reject(Errors.InvalidTarget, $"The resource '{unknown}' is not served here.");
                }

                return default;
            })
            .SetOrder(OpenIddictServerHandlers.Exchange.ValidateResources.Descriptor.Order)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <summary>
    /// RFC 7591 and the MCP client-ID metadata draft, neither of which OpenIddict advertises itself, and
    /// <c>none</c>: every client here is public.
    /// </summary>
    private static readonly OpenIddictServerHandlerDescriptor AdvertiseRegistration =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ApplyConfigurationResponseContext>()
            .UseInlineHandler(context =>
            {
                var issuer = ((string?)context.Response[Metadata.Issuer])?.TrimEnd('/');
                context.Response["registration_endpoint"] = issuer + RegisterPath;
                context.Response[ClientIdMetadataSupported] = true;

                var methods = (StringValues)context.Response[Metadata.TokenEndpointAuthMethodsSupported];
                if (!methods.Contains(ClientAuthenticationMethods.None))
                {
                    context.Response[Metadata.TokenEndpointAuthMethodsSupported] =
                        StringValues.Concat(methods, ClientAuthenticationMethods.None);
                }

                return default;
            })
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <summary>
    /// RFC 7009 §2.1: revoking a refresh token may invalidate the grant, and here it does — the authorization,
    /// every token under it, and every <c>wss_</c> session minted with one of them (see
    /// <c>SessionStore.AuthenticateAsync</c>). That is a client's sign-out. Revoking an access token does not.
    /// </summary>
    private static readonly OpenIddictServerHandlerDescriptor RevokeAuthorizationWithRefreshToken =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.HandleRevocationRequestContext>()
            .UseInlineHandler(async context =>
            {
                if (context.IsRejected
                    || !context.GenericTokenPrincipal.HasTokenType(TokenTypeIdentifiers.RefreshToken)
                    || context.GenericTokenPrincipal.GetAuthorizationId() is not { Length: > 0 } id)
                {
                    return;
                }

                var services = (context.Transaction.GetHttpRequest()
                                ?? throw new InvalidOperationException("No HTTP request on the OpenIddict transaction."))
                    .HttpContext.RequestServices;

                var authorizations = services.GetRequiredService<IOpenIddictAuthorizationManager>();
                if (await authorizations.FindByIdAsync(id, context.CancellationToken) is { } authorization)
                {
                    await authorizations.TryRevokeAsync(authorization, context.CancellationToken);
                    await services.GetRequiredService<IOpenIddictTokenManager>()
                        .RevokeByAuthorizationIdAsync(id, context.CancellationToken);
                }
            })
            .SetOrder(OpenIddictServerHandlers.Revocation.RevokeToken.Descriptor.Order + 1000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <summary>
    /// The audience check, as <c>AddAudiences</c> would make it if the audience were fixed: OpenIddict's own
    /// <c>ValidateAudiences</c> runs right after and refuses a token that names none of these. One audience
    /// per surface, so a token for <c>/mcp</c> is refused on <c>/api</c> and the other way round.
    /// </summary>
    private static readonly OpenIddictValidationHandlerDescriptor RequireSurfaceAudience =
        OpenIddictValidationHandlerDescriptor.CreateBuilder<OpenIddictValidationEvents.ValidateTokenContext>()
            .UseInlineHandler(context =>
            {
                var request = context.Transaction.GetHttpRequest()
                              ?? throw new InvalidOperationException("No HTTP request on the OpenIddict transaction.");

                context.ValidAudiences.Add(ResourceFor(request.HttpContext));
                return default;
            })
            .SetOrder(OpenIddictValidationHandlers.Protection.ValidateAudiences.Descriptor.Order - 500)
            .SetType(OpenIddictValidationHandlerType.Custom)
            .Build();

    /// <summary>The first requested resource that is neither one of this origin's surfaces nor registered.</summary>
    private static string? UnknownResource(OpenIddictServerTransaction transaction, OpenIddictRequest request)
    {
        var known = transaction.Options.Resources.Select(resource => resource.AbsoluteUri)
            .Concat(Surfaces(transaction))
            .ToHashSet(StringComparer.Ordinal);

        return request.GetResources().FirstOrDefault(resource => !known.Contains(resource));
    }

    private static bool BothSurfaces(OpenIddictServerTransaction transaction, OpenIddictRequest request)
        => Surfaces(transaction).All(request.GetResources().Contains);

    private static string[] Surfaces(OpenIddictServerTransaction transaction)
    {
        var http = transaction.GetHttpRequest()
                   ?? throw new InvalidOperationException("No HTTP request on the OpenIddict transaction.");

        var origin = Origin(http.HttpContext);
        return [Resource(origin), ApiResource(origin)];
    }

    private static bool IsLoopbackHttp(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttp
           && uri.IsLoopback;
}
