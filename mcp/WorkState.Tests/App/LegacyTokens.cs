using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using WorkState.Mcp;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Tests.App;

/// <summary>
/// While <see cref="On"/>, the host issues every code, access token and refresh token the way the server did
/// before tokens named a tenant: the subject is the user's name and there is no tenant claim. The tokens Claude
/// Code, Foreman and agent_signin hold from then are of this shape, and the server must still take them.
/// </summary>
internal sealed class LegacyTokens
{
    public bool On { get; set; }

    public static void Add(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<LegacyTokens>();
        builder.Services.AddOpenIddict().AddServer(server => server.AddEventHandler(Shape));
    }

    /// <summary>Ahead of every built-in sign-in handler, so each token is built from the rewritten principal.</summary>
    private static readonly OpenIddictServerHandlerDescriptor Shape =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseInlineHandler(context =>
            {
                var services = (context.Transaction.GetHttpRequest()
                                ?? throw new InvalidOperationException("No HTTP request on the OpenIddict transaction."))
                    .HttpContext.RequestServices;

                if (services.GetRequiredService<LegacyTokens>().On && context.Principal is { } principal)
                {
                    principal.SetClaim(Claims.Subject, principal.GetClaim(Claims.Name))
                        .SetClaim(RequestTenant.Claim, (string?)null);
                }

                return default;
            })
            .SetOrder(int.MinValue + 50_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();
}
