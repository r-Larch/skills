using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WorkState.Mcp;

namespace WorkState.Api.Auth;

public sealed class StaticTokenOptions : AuthenticationSchemeOptions
{
    public string? ReaderToken { get; set; }
    public string? WriterToken { get; set; }
}

/// <summary>
/// Bearer token against two configured values. A writer token also satisfies the reader policy;
/// there is no token that can write but not read.
/// </summary>
public sealed class StaticTokenHandler(
    IOptionsMonitor<StaticTokenOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<StaticTokenOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkStateStaticToken";
    private const string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var raw = header.ToString();
        if (!raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Authorization header must be 'Bearer <token>'."));
        }

        var presented = raw[BearerPrefix.Length..].Trim();

        string[] roles =
            Matches(Options.WriterToken, presented) ? [WorkStatePolicies.Reader, WorkStatePolicies.Writer]
            : Matches(Options.ReaderToken, presented) ? [WorkStatePolicies.Reader]
            : [];

        if (roles.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unrecognised bearer token."));
        }

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.Name, roles.Contains(WorkStatePolicies.Writer) ? "writer" : "reader"));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Length is compared through the digest, so neither the comparison nor its duration leaks it.
    /// A token that is not configured matches nothing — the server fails closed.
    /// </summary>
    private static bool Matches(string? configured, string presented)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(configured), expected);
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), actual);

        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
