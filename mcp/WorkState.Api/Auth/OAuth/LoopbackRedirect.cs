using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// RFC 8252 §7.3: a native client's loopback callback listens on whatever port it got, so the port of a
/// requested <c>http</c> loopback redirect is ignored — scheme, host, path and query must still match a
/// registered callback, and <c>localhost</c> is not <c>127.0.0.1</c>. Every other callback is exact.
/// </summary>
public static class LoopbackRedirect
{
    public static bool Matches(Uri registered, Uri requested)
        => registered.Scheme == Uri.UriSchemeHttp
           && requested.Scheme == Uri.UriSchemeHttp
           && registered.IsLoopback
           && requested.IsLoopback
           && string.Equals(registered.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
           && string.Equals(registered.AbsolutePath, requested.AbsolutePath, StringComparison.Ordinal)
           && string.Equals(registered.Query, requested.Query, StringComparison.Ordinal)
           && string.Equals(registered.UserInfo, requested.UserInfo, StringComparison.Ordinal)
           && string.Equals(registered.Fragment, requested.Fragment, StringComparison.Ordinal);
}

/// <summary>
/// <see cref="LoopbackRedirect"/> at the one place OpenIddict checks a requested redirect against the
/// registration, for registered and metadata clients alike. OpenIddict's own leniency covers only a
/// <c>native</c> application whose callback has no port, which no row here is. The token endpoint needs
/// nothing: it compares the redirect with the one the code was issued to, exactly.
/// </summary>
public sealed class WorkStateApplicationManager(
    IOpenIddictApplicationCache<OpenIddictEntityFrameworkCoreApplication> cache,
    ILogger<OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication>> logger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication> store)
    : OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication>(cache, logger, options, store)
{
    public override async ValueTask<bool> ValidateRedirectUriAsync(
        OpenIddictEntityFrameworkCoreApplication application,
        string uri,
        CancellationToken cancellationToken = default)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var requested)
            && await HasClientTypeAsync(application, ClientTypes.Public, cancellationToken))
        {
            foreach (var candidate in await GetRedirectUrisAsync(application, cancellationToken))
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var registered)
                    && LoopbackRedirect.Matches(registered, requested))
                {
                    return true;
                }
            }
        }

        return await base.ValidateRedirectUriAsync(application, uri, cancellationToken);
    }
}
