using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace WorkState.Api.Auth;

/// <summary>
/// The two anonymous writes: signing in, which guesses at the operator's password, and registering an OAuth
/// client, which writes an application row nothing prunes. Per client address, in memory — one process, one
/// operator — and keyed on the address <see cref="TrustedProxies"/> has already resolved, so a named proxy
/// does not put every caller in one partition.
/// </summary>
public static class RateLimits
{
    public const string SignIn = "sign-in";
    public const string Registration = "registration";

    public static readonly FixedWindowRateLimiterOptions SignInWindow = new()
    {
        PermitLimit = 5,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    public static readonly FixedWindowRateLimiterOptions RegistrationWindow = new()
    {
        PermitLimit = 10,
        Window = TimeSpan.FromHours(1),
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
            options.AddPolicy(Registration, context => RateLimitPartition.GetFixedWindowLimiter(Client(context), _ => RegistrationWindow));
        });

    private static string Client(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
}
