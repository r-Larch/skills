using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using WorkState.Mcp;

namespace WorkState.Api.Auth;

/// <summary>
/// The bootstrap credential (doc 08 §4): the first sign-in that presents it while <c>app_user</c> is empty
/// creates the first user from it (<see cref="UserStore"/>), and after that the table decides and this
/// password is never read again. Blank in <c>appsettings.json</c>: a password committed to a public
/// repository is a password on the internet, and the throwaway bearer tokens are already as far as that goes.
/// </summary>
public sealed class DashboardOptions
{
    public const string Section = "WorkState:Dashboard";

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// No credential configured and no user yet means no login and no <c>/api</c>, exactly as an unconfigured
    /// bearer token means no <c>/mcp</c>. The dashboard is off until someone turns it on, rather
    /// than open until someone secures it. The OAuth server does not read it: its gate is a signed-in user.
    /// </summary>
    public bool Configured
        => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Both halves are compared through their digests, so neither the comparison nor its duration
    /// leaks the configured value. <see cref="StaticTokenHandler"/> is the precedent.
    /// </summary>
    public bool Matches(string? username, string? password)
        => Configured
           && FixedTimeEquals(Username, username)
           && FixedTimeEquals(Password, password);

    private static bool FixedTimeEquals(string? configured, string? presented)
    {
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(configured ?? string.Empty), expected);
        SHA256.HashData(Encoding.UTF8.GetBytes(presented ?? string.Empty), actual);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

public static class DashboardAuth
{
    /// <summary>The cookie scheme's name. Deliberately not <c>Cookies</c>: the default name is
    /// what an <c>AddAuthentication()</c> elsewhere would silently adopt.</summary>
    public const string SchemeName = "WorkStateDashboard";

    public const string CookieName = "workstate.dashboard";

    /// <summary>
    /// The policy every pre-v1 <c>/api</c> route carries. It names the cookie scheme and only the
    /// cookie scheme, which is the whole of doc 08 §4's "two auth schemes that never cross": a bearer
    /// token presented to those routes is not evaluated by a handler that could accept it, and the
    /// three bearer policies likewise name only <see cref="StaticTokenHandler.SchemeName"/>. The
    /// read-only <c>/api/v1</c> and <c>/api/stream</c> are the deliberate exception (doc 10 rule 5).
    /// </summary>
    public const string Policy = "dashboard";

    public const string Role = "dashboard-operator";

    /// <summary>The <c>app_user</c> id, which is what <see cref="Configure"/> re-checks on every request.</summary>
    public const string UserIdClaim = "sub";

    public const string SystemAdminClaim = "workstate:system_admin";

    /// <summary>The session of <paramref name="user"/> in <paramref name="tenant"/>, the one it switches to.</summary>
    public static ClaimsPrincipal Principal(DashboardUser user, Guid tenant)
    {
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(UserIdClaim, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Name));
        identity.AddClaim(new Claim(ClaimTypes.Role, Role));
        identity.AddClaim(new Claim(SystemAdminClaim, user.IsSystemAdmin ? "true" : "false", ClaimValueTypes.Boolean));
        identity.AddClaim(new Claim(RequestTenant.Claim, tenant.ToString()));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// An API is not a login page: an unauthenticated <c>/api</c> call gets 401 and a forbidden one
    /// 403, never a 302 to HTML the SPA would have to parse to discover it was signed out.
    /// </summary>
    public static void Configure(CookieAuthenticationOptions options, string? publicUrl = null)
    {
        options.Cookie.Name = CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;

        // Local installs are plain HTTP on loopback, so the default follows the request — which, behind
        // a trusted proxy, is the forwarded scheme. An https public URL forces it: a proxy left out of
        // WORKSTATE_TRUSTED_PROXIES must not quietly downgrade the session cookie on a TLS origin.
        options.Cookie.SecurePolicy = IsHttps(publicUrl)
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);

        // Every request asks the table, so disabling or deleting a user, or ending their membership of the
        // cookie's tenant, ends the session there rather than leaving it open until the cookie expires. A
        // cookie with no user id or no tenant predates them and signs in again. The configured credential is
        // not asked: once a user exists it no longer decides anything.
        options.Events.OnValidatePrincipal = async context =>
        {
            var users = context.HttpContext.RequestServices.GetService<UserStore>();
            var user = users is not null
                       && Guid.TryParse(context.Principal?.FindFirstValue(UserIdClaim), out var id)
                       && RequestTenant.Of(context.Principal) is { } tenant
                ? await users.EnterAsync(id, tenant, context.HttpContext.RequestAborted)
                : null;

            if (user is null)
            {
                context.RejectPrincipal();
                return;
            }

            // A rename or a changed administrator flag reaches the session on its next request.
            var current = Principal(user, RequestTenant.Of(context.Principal)!.Value);
            if (current.Identity?.Name != context.Principal!.Identity?.Name
                || current.FindFirstValue(SystemAdminClaim) != context.Principal.FindFirstValue(SystemAdminClaim))
            {
                context.ReplacePrincipal(current);
                context.ShouldRenew = true;
            }
        };

        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    private static bool IsHttps(string? publicUrl)
        => Uri.TryCreate(publicUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
