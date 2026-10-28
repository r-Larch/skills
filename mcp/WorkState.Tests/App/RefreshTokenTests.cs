using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using WorkState.Tests.Store;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// m57: a refresh token is an opaque handle to a row of <c>openiddict_token</c>, short enough for the 2560-byte
/// blob Windows Credential Manager stores, where Foreman keeps it. One the server issued before, a self-contained
/// JWE, still redeems — into a handle — so no client has to sign in again. Revocation and reuse behave as they did.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RefreshTokenTests(PostgresFixture fixture)
{
    /// <summary>What <c>CredWriteW</c> takes as a credential blob (<c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c>).</summary>
    private const int CredentialBlobLimit = 2560;

    private const string PublicUrl = "http://127.0.0.1:5998";

    [Fact]
    public async Task A_RefreshToken_is_an_opaque_handle_under_100_characters_that_refreshes_and_rotates()
    {
        await using var server = await StartAsync(Slug(), reference: true);
        var tokens = await SignInAsync(server);

        var first = tokens.RefreshToken!;
        Report($"reference refresh token: {first.Length} characters");
        AssertHandle(first);
        Assert.Equal(Statuses.Valid, await StatusAsync(server, first));

        var refreshed = await OAuthTests.RefreshAsync(server.NewClient(), tokens.ClientId, first);
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, refreshed.AccessToken)).StatusCode);

        AssertHandle(refreshed.RefreshToken!);
        Assert.NotEqual(first, refreshed.RefreshToken);
        Assert.Equal(Statuses.Redeemed, await StatusAsync(server, first));
        Assert.Equal(Statuses.Valid, await StatusAsync(server, refreshed.RefreshToken!));
    }

    [Fact]
    public async Task A_self_contained_RefreshToken_from_before_the_switch_redeems_into_a_handle()
    {
        var slug = Slug();
        OAuthTests.TokenSet legacy;

        await using (var before = await StartAsync(slug, reference: false))
        {
            legacy = await SignInAsync(before);
        }

        var old = legacy.RefreshToken!;
        Report($"1.0.0-style refresh token: {old.Length} characters (blob limit {CredentialBlobLimit} bytes)");
        AssertSelfContained(old);

        await using var after = await StartAsync(slug, reference: true);
        var upgraded = await OAuthTests.RefreshAsync(after.NewClient(), legacy.ClientId, old);

        AssertHandle(upgraded.RefreshToken!);
        Assert.Equal(Statuses.Valid, await StatusAsync(after, upgraded.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(after, upgraded.AccessToken)).StatusCode);

        var again = await OAuthTests.RefreshAsync(after.NewClient(), legacy.ClientId, upgraded.RefreshToken!);
        AssertHandle(again.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(after, again.AccessToken)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoking_a_RefreshToken_ends_its_authorization_and_the_handle_under_it(bool legacy)
    {
        var slug = Slug();
        var tokens = await IssueAsync(slug, legacy);

        await using var server = await StartAsync(slug, reference: true);
        var handle = (await OAuthTests.RefreshAsync(server.NewClient(), tokens.ClientId, tokens.RefreshToken!)).RefreshToken!;

        // The sign-out agent_signout does: the handle names the authorization RevokeAuthorizationWithRefreshToken ends.
        var revoked = await server.NewClient().PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = handle,
                ["token_type_hint"] = TokenTypeHints.RefreshToken,
                ["client_id"] = tokens.ClientId,
            }),
            Ct);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        Assert.Equal(Statuses.Revoked, await AuthorizationStatusAsync(server, handle));
        await AssertRefusedAsync(server, tokens.ClientId, handle);
    }

    [Fact]
    public async Task A_revoked_authorization_ends_its_reference_RefreshToken()
    {
        await using var server = await StartAsync(Slug(), reference: true);
        var tokens = await SignInAsync(server);

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var authorization = await manager.FindByIdAsync((await AuthorizationIdAsync(server, tokens.RefreshToken!))!, Ct);
            Assert.True(await manager.TryRevokeAsync(authorization!, Ct));
        }

        await AssertRefusedAsync(server, tokens.ClientId, tokens.RefreshToken!);
    }

    /// <summary>
    /// What the server did before: a redeemed refresh token works again inside OpenIddict's reuse leeway (30 seconds,
    /// for a client's concurrent refreshes), and past it is refused and takes every token of its authorization along.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_redeemed_RefreshToken_is_reusable_only_inside_the_leeway_in_either_format(bool legacy)
    {
        var slug = Slug();
        var tokens = await IssueAsync(slug, legacy);
        await using (var server = await StartAsync(slug, reference: true))
        {
            var once = await OAuthTests.RefreshAsync(server.NewClient(), tokens.ClientId, tokens.RefreshToken!);
            var twice = await OAuthTests.RefreshAsync(server.NewClient(), tokens.ClientId, tokens.RefreshToken!);
            AssertHandle(once.RefreshToken!);
            AssertHandle(twice.RefreshToken!);
        }

        var later = Slug();
        tokens = await IssueAsync(later, legacy, leeway: TimeSpan.Zero);
        await using (var server = await StartAsync(later, reference: true, leeway: TimeSpan.Zero))
        {
            var successor = await OAuthTests.RefreshAsync(server.NewClient(), tokens.ClientId, tokens.RefreshToken!);

            await AssertRefusedAsync(server, tokens.ClientId, tokens.RefreshToken!);
            Assert.Equal(Statuses.Revoked, await StatusAsync(server, successor.RefreshToken!));
            await AssertRefusedAsync(server, tokens.ClientId, successor.RefreshToken!);
        }
    }

    /// <summary>A sign-in on a host configured as the server was before (<paramref name="legacy"/>) or as it is.</summary>
    private async Task<OAuthTests.TokenSet> IssueAsync(string slug, bool legacy, TimeSpan? leeway = null)
    {
        await using var server = await StartAsync(slug, reference: !legacy, leeway);
        var tokens = await SignInAsync(server);
        if (legacy)
        {
            AssertSelfContained(tokens.RefreshToken!);
        }
        else
        {
            AssertHandle(tokens.RefreshToken!);
        }

        return tokens;
    }

    /// <summary>
    /// One deployment across a restart: the public URL and the database are what two hosts share. With
    /// <paramref name="reference"/> false the host issues refresh tokens as 1.0.0 did, self-contained; with true it
    /// runs the server's own configuration, so a test of it fails if that stops issuing handles.
    /// </summary>
    private Task<AppServer> StartAsync(string slug, bool reference, TimeSpan? leeway = null)
        => AppServer.StartAsync(
            fixture.ConnectionString,
            slug,
            Ct,
            publicUrl: PublicUrl,
            configure: builder => builder.Services.PostConfigure<OpenIddictServerOptions>(options =>
            {
                if (!reference)
                {
                    options.UseReferenceRefreshTokens = false;
                }

                if (leeway is { } value)
                {
                    options.RefreshTokenReuseLeeway = value;
                }
            }));

    private static async Task<OAuthTests.TokenSet> SignInAsync(AppServer server)
    {
        var anonymous = server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous);
        var browser = await server.SignInAsync(Ct);
        var pkce = OAuthTests.Pkce.Create();
        var code = await OAuthTests.AuthorizeForCodeAsync(browser, clientId, pkce, $"{PublicUrl}/mcp");
        return await OAuthTests.ExchangeAsync(anonymous, clientId, code, pkce);
    }

    private static async Task AssertRefusedAsync(AppServer server, string clientId, string refreshToken)
    {
        var refused = await OAuthTests.TokenAsync(server.NewClient(), new()
        {
            ["grant_type"] = GrantTypes.RefreshToken,
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(Errors.InvalidGrant, (await OAuthTests.ReadAsync(refused)).GetProperty("error").GetString());
    }

    private static void AssertHandle(string refreshToken)
    {
        Assert.True(refreshToken.Length < 100, $"refresh token of {refreshToken.Length} characters");
        Assert.DoesNotContain('.', refreshToken);
    }

    /// <summary>A compact JWE: header, key, IV, ciphertext, tag.</summary>
    private static void AssertSelfContained(string refreshToken)
        => Assert.Equal(5, refreshToken.Split('.').Length);

    private static Task<string?> StatusAsync(AppServer server, string handle)
        => WithTokenAsync(server, handle, (tokens, token) => tokens.GetStatusAsync(token, Ct));

    private static Task<string?> AuthorizationIdAsync(AppServer server, string handle)
        => WithTokenAsync(server, handle, (tokens, token) => tokens.GetAuthorizationIdAsync(token, Ct));

    private static async Task<string?> AuthorizationStatusAsync(AppServer server, string handle)
    {
        var id = await AuthorizationIdAsync(server, handle);
        await using var scope = server.Services.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        return await authorizations.GetStatusAsync((await authorizations.FindByIdAsync(id!, Ct))!, Ct);
    }

    private static async Task<string?> WithTokenAsync(
        AppServer server,
        string handle,
        Func<IOpenIddictTokenManager, object, ValueTask<string?>> read)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await tokens.FindByReferenceIdAsync(handle, Ct);
        Assert.NotNull(token);
        return await read(tokens, token);
    }

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static string Slug() => $"refresh-{Guid.CreateVersion7():n}";
}
