using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using WorkState.Api.App;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace WorkState.Api.Auth.OAuth;

/// <summary>
/// The two pages a human meets inside <c>/connect/authorize</c>, both SPA routes
/// (<c>frontend/src/Connect.tsx</c>) whose shell is served only after the request is judged here: the
/// sign-in page refuses a return address that is not this server's authorize endpoint, and the consent page
/// is sent back through sign-in when the session is gone.
/// <para>
/// Both forbid framing. The consent page's <c>form-action</c> names the client's registered callbacks, the
/// request's own when it differs from one only in a loopback port, and <c>'self'</c>: a browser checks the
/// redirect a form submission ends in against that directive, and the approval's answer is a redirect to
/// the client — on whatever port its listener got. Its <c>img-src</c> is this origin: the client's
/// icon comes from <see cref="AppIconPath"/>, never from the client's host or a favicon service.
/// </para>
/// </summary>
public static class ConnectPages
{
    public const string SignInPath = "/connect/signin";
    public const string ConsentPath = "/connect/consent";
    public const string AppInfoPath = "/connect/app-info";
    public const string AppIconPath = "/connect/app-icon";

    public const string ReturnUrl = "returnUrl";
    public const string ConsentField = "consent";
    public const string Granted = "granted";

    public static void MapConnectPages(this WebApplication app)
    {
        app.MapGet(SignInPath, SignInAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(ConsentPath, ConsentAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(AppInfoPath, AppInfoAsync).RequireAuthorization(DashboardAuth.Policy).ExcludeFromDescription();
        app.MapGet(AppIconPath, AppIconAsync)
            .RequireAuthorization(DashboardAuth.Policy)
            .RequireRateLimiting(RateLimits.AppIcon)
            .ExcludeFromDescription();
    }

    /// <summary>
    /// Same-origin and the authorize endpoint only, so the sign-in page cannot be made to forward a fresh
    /// session anywhere else. <c>frontend/src/connect.ts</c> applies the same rule before it navigates.
    /// </summary>
    public static bool IsReturnUrl(string? value)
        => value is not null
           && (value == OAuthServer.AuthorizePath
               || value.StartsWith(OAuthServer.AuthorizePath + "?", StringComparison.Ordinal));

    private static async Task SignInAsync(HttpContext context)
    {
        if (!IsReturnUrl(context.Request.Query[ReturnUrl].FirstOrDefault()))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                $"{ReturnUrl} must be this server's {OAuthServer.AuthorizePath}.", context.RequestAborted);
            return;
        }

        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        await SpaFallback.ServeAsync(context);
    }

    private static async Task ConsentAsync(HttpContext context, IOpenIddictApplicationManager applications)
    {
        var session = await context.AuthenticateAsync(DashboardAuth.SchemeName);
        if (!session.Succeeded || !session.Principal.IsInRole(DashboardAuth.Role))
        {
            var back = OAuthServer.AuthorizePath + context.Request.QueryString;
            context.Response.Redirect($"{SignInPath}?{ReturnUrl}={Uri.EscapeDataString(back)}");
            return;
        }

        var callbacks = await CallbacksAsync(applications, context.Request.Query[Parameters.ClientId].FirstOrDefault(), context.RequestAborted);
        var requested = Registered(callbacks, context.Request.Query[Parameters.RedirectUri].FirstOrDefault());
        var origins = callbacks
            .Select(callback => Uri.TryCreate(callback, UriKind.Absolute, out var uri) ? uri : null)
            .Append(requested)
            .OfType<Uri>()
            .Select(uri => uri.GetLeftPart(UriPartial.Authority))
            .Distinct(StringComparer.Ordinal);

        context.Response.Headers.ContentSecurityPolicy =
            $"form-action {string.Join(' ', origins.Prepend("'self'"))}; frame-ancestors 'none'; img-src 'self' data:";
        await SpaFallback.ServeAsync(context);
    }

    private static async Task<IResult> AppInfoAsync(
        HttpContext context,
        IOpenIddictApplicationManager applications)
    {
        var clientId = context.Request.Query[Parameters.ClientId].FirstOrDefault();
        if (string.IsNullOrEmpty(clientId))
        {
            return Results.BadRequest();
        }

        var application = await applications.FindByClientIdAsync(clientId, context.RequestAborted);
        if (application is null)
        {
            return Results.NotFound();
        }

        var callbacks = await applications.GetRedirectUrisAsync(application, context.RequestAborted);
        return Results.Ok(new AppInfo(
            await applications.GetDisplayNameAsync(application, context.RequestAborted) ?? clientId,
            ClientIdMetadata.IsMetadataDocument(clientId, out var document) ? document.Host : null,
            Registered(callbacks, context.Request.Query[Parameters.RedirectUri].FirstOrDefault())?.Host,
            callbacks.Length > 0 && callbacks.All(callback => Uri.TryCreate(callback, UriKind.Absolute, out var uri) && uri.IsLoopback)));
    }

    /// <summary>
    /// A 404 when the client has no row — so this is no fetch proxy for any id a page names — or when nothing
    /// it declared resolves, and the page shows its initials instead.
    /// </summary>
    private static async Task<IResult> AppIconAsync(
        HttpContext context,
        IOpenIddictApplicationManager applications,
        ClientIconResolver icons)
    {
        var clientId = context.Request.Query[Parameters.ClientId].FirstOrDefault();
        if (string.IsNullOrEmpty(clientId))
        {
            return Results.BadRequest();
        }

        if (await applications.FindByClientIdAsync(clientId, context.RequestAborted) is not { } application
            || await icons.ResolveAsync(applications, application, context.RequestAborted) is not { } icon)
        {
            return Results.NotFound();
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=3600";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.Bytes(icon.Body, icon.ContentType);
    }

    /// <summary>
    /// The request's callback when it is one this client registered, or differs from one only in a loopback
    /// port (<see cref="LoopbackRedirect"/>, as authorize admits it): echoing an unchecked <c>redirect_uri</c>
    /// would put an attacker's host on the page, or in <c>form-action</c>, under the real client's name.
    /// </summary>
    private static Uri? Registered(IReadOnlyList<string> callbacks, string? redirectUri)
        => Uri.TryCreate(redirectUri, UriKind.Absolute, out var requested)
           && callbacks.Any(callback => callback == redirectUri
                                        || (Uri.TryCreate(callback, UriKind.Absolute, out var registered)
                                            && LoopbackRedirect.Matches(registered, requested)))
            ? requested
            : null;

    private static async Task<IReadOnlyList<string>> CallbacksAsync(
        IOpenIddictApplicationManager applications,
        string? clientId,
        CancellationToken cancellationToken)
        => !string.IsNullOrEmpty(clientId)
           && await applications.FindByClientIdAsync(clientId, cancellationToken) is { } application
            ? await applications.GetRedirectUrisAsync(application, cancellationToken)
            : [];

    /// <param name="ClientName">The registration's <c>client_name</c>, or the metadata document's.</param>
    /// <param name="ClientHost">The host serving the metadata document; null for a client that registered itself.</param>
    /// <param name="RedirectHost">Null unless the request's callback is registered; see <see cref="Registered"/>.</param>
    /// <param name="LoopbackOnly">Every callback is on this machine, so the client is a local program.</param>
    public sealed record AppInfo(string ClientName, string? ClientHost, string? RedirectHost, bool LoopbackOnly);
}
