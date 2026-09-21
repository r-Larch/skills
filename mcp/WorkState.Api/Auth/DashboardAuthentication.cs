using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace WorkState.Api.Auth;

/// <summary>
/// The operator's credentials, from configuration only (doc 08 §4). Blank in
/// <c>appsettings.json</c>: a password committed to a public repository is a password on the
/// internet, and the throwaway bearer tokens are already as far as that goes.
/// </summary>
public sealed class DashboardOptions
{
    public const string Section = "WorkState:Dashboard";

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// No credential configured means no login and no <c>/api</c>, exactly as an unconfigured
    /// bearer token means no <c>/mcp</c>. The dashboard is off until someone turns it on, rather
    /// than open until someone secures it.
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
    /// The policy every <c>/api</c> route carries. It names the cookie scheme and only the cookie
    /// scheme, which is the whole of doc 08 §4's "two auth schemes that never cross": a bearer
    /// token presented to <c>/api</c> is not evaluated by a handler that could accept it, and the
    /// three bearer policies likewise name only <see cref="StaticTokenHandler.SchemeName"/>.
    /// </summary>
    public const string Policy = "dashboard";

    public const string Role = "dashboard-operator";

    public static ClaimsPrincipal Principal(string username)
    {
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.Name, username));
        identity.AddClaim(new Claim(ClaimTypes.Role, Role));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// An API is not a login page: an unauthenticated <c>/api</c> call gets 401 and a forbidden one
    /// 403, never a 302 to HTML the SPA would have to parse to discover it was signed out.
    /// </summary>
    public static void Configure(CookieAuthenticationOptions options, DashboardOptions dashboard)
    {
        options.Cookie.Name = CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;

        // doc 08 §4 keeps the API bound to localhost over plain HTTP, so requiring a secure cookie
        // here would lock the operator out of the only deployment that exists.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);

        // Fail closed even against a cookie issued while a credential *was* configured: removing
        // the credential is how an operator turns the dashboard off, and a surviving session would
        // make that switch a no-op until the cookie happened to expire.
        options.Events.OnValidatePrincipal = context =>
        {
            if (!dashboard.Configured)
            {
                context.RejectPrincipal();
            }

            return Task.CompletedTask;
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
}
