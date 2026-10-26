using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WorkState.Core.Writing;
using ActorRole = WorkState.Core.Domain.ActorRole;
using WorkState.Mcp;

namespace WorkState.Api.Auth;

public sealed class StaticTokenOptions : AuthenticationSchemeOptions
{
    /// <summary>doc 01 §6. Nested, not parallel: a controller token is also an orchestrator and a worker.</summary>
    public string? WorkerToken { get; set; }

    public string? OrchestratorToken { get; set; }
    public string? ControllerToken { get; set; }

    /// <summary>doc 10 rule 5. Optional; blank means no reader token exists.</summary>
    public string? ReaderToken { get; set; }
}

/// <summary>
/// Bearer token against the configured values. Roles are nested — a writer also satisfies the
/// reader policy, a controller satisfies every profile — so there is no token that can act at one
/// tier and not read at a lower one.
/// </summary>
public sealed class StaticTokenHandler(
    IOptionsMonitor<StaticTokenOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<StaticTokenOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkStateStaticToken";
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var raw = header.ToString();
        if (!raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Authorization header must be 'Bearer <token>'.");
        }

        var presented = raw[BearerPrefix.Length..].Trim();

        string[] roles =
            Matches(Options.ControllerToken, presented) ? Nested(ActorRole.Controller)
            : Matches(Options.OrchestratorToken, presented) ? Nested(ActorRole.Orchestrator)
            : Matches(Options.WorkerToken, presented) ? Nested(ActorRole.Worker)
            : Matches(Options.ReaderToken, presented) ? [WorkStatePolicies.Reader]
            : [];

        if (roles.Length > 0)
        {
            return AuthenticateResult.Success(Ticket(Identity(roles)));
        }

        return presented.StartsWith(SessionToken.Prefix, StringComparison.Ordinal)
            ? await MintedAsync(presented)
            : AuthenticateResult.Fail("Unrecognised bearer token.");
    }

    /// <summary>
    /// doc 14 §2 — a minted token is looked up by its hash; unknown, revoked and expired fail alike.
    /// The principal carries the granted role, nested as the static ones are, and the session, which
    /// is what binds the name to the credential instead of to a header.
    /// </summary>
    private async Task<AuthenticateResult> MintedAsync(string presented)
    {
        if (Context.RequestServices.GetService<SessionStore>() is not { } store
            || await store.AuthenticateAsync(presented, Context.RequestAborted) is not { } session)
        {
            return AuthenticateResult.Fail("Unrecognised bearer token.");
        }

        var identity = Identity(Nested(session.Role));
        identity.AddClaim(new Claim(CallerSession.SessionIdClaim, session.SessionId.ToString()));
        identity.AddClaim(new Claim(CallerSession.SessionNameClaim, session.Name));
        foreach (var grant in session.Grants)
        {
            identity.AddClaim(new Claim(CallerSession.GrantClaim, grant));
        }

        return AuthenticateResult.Success(Ticket(identity));
    }

    public static string[] Nested(ActorRole role) => role switch
    {
        ActorRole.Controller => [WorkStatePolicies.Worker, WorkStatePolicies.Orchestrator, WorkStatePolicies.Controller],
        ActorRole.Orchestrator => [WorkStatePolicies.Worker, WorkStatePolicies.Orchestrator],
        ActorRole.Worker => [WorkStatePolicies.Worker],
        _ => [],
    };

    private static ClaimsIdentity Identity(string[] roles)
    {
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.Name, roles[^1]));
        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return identity;
    }

    private AuthenticationTicket Ticket(ClaimsIdentity identity) => new(new ClaimsPrincipal(identity), Scheme.Name);

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
