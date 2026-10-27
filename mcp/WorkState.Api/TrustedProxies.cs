using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace WorkState.Api;

/// <summary>
/// The addresses whose <c>X-Forwarded-For/Proto/Host</c> this process believes: comma-separated IPs
/// or CIDRs. Empty trusts nobody and leaves the forwarded-headers middleware out of the pipeline
/// entirely — its own defaults trust loopback, and a local install has no proxy to trust.
/// </summary>
public static class TrustedProxies
{
    public const string Key = "WorkState:TrustedProxies";

    public const string EnvironmentKey = "WORKSTATE_TRUSTED_PROXIES";

    public static string? Read(IConfiguration configuration)
        => configuration[Key] is { Length: > 0 } value ? value : configuration[EnvironmentKey];

    /// <summary>
    /// Null when nothing is configured. A malformed entry throws at startup rather than being skipped:
    /// a typo that silently trusts nothing turns every link and cookie back to plain http.
    /// </summary>
    public static ForwardedHeadersOptions? Options(string? configured)
    {
        var entries = (configured ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            return null;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in entries)
        {
            if (entry.Contains('/'))
            {
                if (!System.Net.IPNetwork.TryParse(entry, out var network))
                {
                    throw new InvalidOperationException($"{EnvironmentKey}: '{entry}' is not a CIDR network.");
                }

                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException($"{EnvironmentKey}: '{entry}' is not an IP address or CIDR network.");
            }
        }

        return options;
    }
}
