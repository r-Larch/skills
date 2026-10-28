using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WorkState.Api.Auth;

/// <summary>
/// The anonymous writes: signing in, which guesses at the operator's password; following an invite or reset
/// link, which guesses at its token; and registering an OAuth client, which writes an application row nothing
/// prunes; and the consent page's client icon, which makes this server fetch from the client's host. Per client
/// address, in memory — one process, one operator — and keyed on the address <see cref="TrustedProxies"/> has
/// already resolved, so a named proxy does not put every caller in one partition.
/// </summary>
public static class RateLimits
{
    public const string SignIn = "sign-in";
    public const string Link = "link";
    public const string Registration = "registration";
    public const string AppIcon = "app-icon";

    public static readonly FixedWindowRateLimiterOptions SignInWindow = new()
    {
        PermitLimit = 5,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    /// <summary>Sign-in's window, doubled: a link's page spends one request reading it before the one that uses it.</summary>
    public static readonly FixedWindowRateLimiterOptions LinkWindow = new()
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    public static readonly FixedWindowRateLimiterOptions RegistrationWindow = new()
    {
        PermitLimit = 10,
        Window = TimeSpan.FromHours(1),
        QueueLimit = 0,
    };

    public static readonly FixedWindowRateLimiterOptions AppIconWindow = new()
    {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    public static void Add(IServiceCollection services)
        => services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            options.AddPolicy(SignIn, context => RateLimitPartition.GetFixedWindowLimiter(Client(context), _ => SignInWindow));
            options.AddPolicy(Link, context => RateLimitPartition.GetFixedWindowLimiter(Client(context), _ => LinkWindow));
            options.AddPolicy(Registration, context => RateLimitPartition.GetFixedWindowLimiter(Client(context), _ => RegistrationWindow));
            options.AddPolicy(AppIcon, context => RateLimitPartition.GetFixedWindowLimiter(Client(context), _ => AppIconWindow));
        });

    private static string Client(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}
