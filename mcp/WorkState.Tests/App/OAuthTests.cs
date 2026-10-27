using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The OAuth server end to end, in-process on a loopback port: the challenge an anonymous MCP client
/// gets, the two metadata documents it follows, registration, the authorization code with PKCE S256,
/// the exchange, <c>tools/list</c> with the token, refresh and revocation — and the refusals that make
/// the token mean something: no plain PKCE, no token for another resource, no expired or altered token.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class OAuthTests(PostgresFixture fixture)
{
    private const string Callback = "http://127.0.0.1:7777/callback";
    private const string Elsewhere = "https://elsewhere.example/mcp";

    [Fact]
    public async Task An_mcp_client_goes_from_a_401_to_the_controller_tools_and_back_out()
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var anonymous = server.NewClient();

        // c1: the challenge names the path-scoped metadata, which names this origin as its server.
        var challenged = await anonymous.SendAsync(AppServer.McpInitialize(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        var metadataUrl = ResourceMetadataUrl(challenged);
        Assert.Equal($"{server.BaseAddress}/.well-known/oauth-protected-resource/mcp", metadataUrl);

        var resource = await GetJsonAsync(anonymous, metadataUrl);
        Assert.Equal($"{server.BaseAddress}/mcp", resource.GetProperty("resource").GetString());
        Assert.Equal(server.BaseAddress, Assert.Single(Strings(resource, "authorization_servers")));

        var discovery = await GetJsonAsync(anonymous, "/.well-known/oauth-authorization-server");
        Assert.Equal($"{server.BaseAddress}/", discovery.GetProperty("issuer").GetString());
        Assert.Equal($"{server.BaseAddress}/connect/register", discovery.GetProperty("registration_endpoint").GetString());
        Assert.Equal(["S256"], Strings(discovery, "code_challenge_methods_supported"));
        Assert.Contains("none", Strings(discovery, "token_endpoint_auth_methods_supported"));
        Assert.True(discovery.GetProperty("client_id_metadata_document_supported").GetBoolean());

        // c2: register, authorize, exchange.
        var clientId = await RegisterAsync(anonymous);
        var browser = await server.SignInAsync(Ct);
        var pkce = Pkce.Create();
        var code = await AuthorizeForCodeAsync(browser, clientId, pkce, $"{server.BaseAddress}/mcp");
        var tokens = await ExchangeAsync(anonymous, clientId, code, pkce);

        // The OAuth principal is the controller's: the same tools a static controller token lists.
        await using (var oauth = await server.ConnectAsync(tokens.AccessToken, Ct))
        await using (var controller = await server.ConnectAsync(ReadServer.ControllerToken, Ct))
        {
            var viaOAuth = await ToolNamesAsync(oauth);
            Assert.Contains("work_brief", viaOAuth);
            Assert.Equal(await ToolNamesAsync(controller), viaOAuth);
        }

        var refreshed = await RefreshAsync(anonymous, clientId, tokens.RefreshToken!);
        Assert.NotEqual(tokens.AccessToken, refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(server, refreshed.AccessToken)).StatusCode);

        // Revoked, the access token stops working now, not at its expiry.
        await RevokeAsync(anonymous, clientId, refreshed.AccessToken);
        var revoked = await McpAsync(server, refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        await RevokeAsync(anonymous, clientId, refreshed.RefreshToken!);
        var refused = await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshed.RefreshToken!,
            ["client_id"] = clientId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_grant", (await ReadAsync(refused)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("plain")]
    [InlineData(null)]
    public async Task Pkce_other_than_S256_is_refused(string? method)
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var clientId = await RegisterAsync(server.NewClient());
        var browser = await server.SignInAsync(Ct);
        var verifier = Pkce.Create().Verifier;

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Callback,
            ["scope"] = "mcp offline_access",
            ["state"] = "s",
            ["code_challenge"] = verifier,
            ["code_challenge_method"] = method,
        };

        var response = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", query), Ct);
        await AssertRefusedAsync(response, "invalid_request");
    }

    [Fact]
    public async Task A_token_for_another_resource_is_refused_by_mcp()
    {
        // The resource is registered for this host only so that a token for it can be issued at all;
        // /mcp must still refuse it, which is the audience check and nothing else.
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            Slug(),
            Ct,
            configure: builder => builder.Services.Configure<OpenIddictServerOptions>(
                options => options.Resources.Add(new Uri(Elsewhere))));

        var anonymous = server.NewClient();
        var clientId = await RegisterAsync(anonymous);
        var browser = await server.SignInAsync(Ct);

        var pkce = Pkce.Create();
        var other = await ExchangeAsync(anonymous, clientId, await AuthorizeForCodeAsync(browser, clientId, pkce, Elsewhere), pkce);
        var refused = await McpAsync(server, other.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // The same flow for this server's /mcp is admitted, so the refusal is about the audience alone.
        pkce = Pkce.Create();
        var own = await ExchangeAsync(anonymous, clientId, await AuthorizeForCodeAsync(browser, clientId, pkce, $"{server.BaseAddress}/mcp"), pkce);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(server, own.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_resource_this_server_does_not_serve_is_refused_at_authorize()
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var clientId = await RegisterAsync(server.NewClient());
        var browser = await server.SignInAsync(Ct);

        var response = await browser.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), Elsewhere), Ct);
        await AssertRefusedAsync(response, "invalid_target");
    }

    /// <summary>
    /// OpenIddict refuses some requests before it has validated the redirect URI, and those it answers
    /// itself rather than redirecting; either way the error is named and no code is issued.
    /// </summary>
    private static async Task AssertRefusedAsync(HttpResponseMessage response, string error)
    {
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            var location = QueryHelpers.ParseQuery(response.Headers.Location?.Query);
            Assert.Equal(error, location["error"].ToString());
            Assert.False(location.ContainsKey("code"));
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_altered_token_is_refused()
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var tokens = await FullFlowAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(server, tokens.AccessToken)).StatusCode);

        var segments = tokens.AccessToken.Split('.');
        var middle = segments[^2].ToCharArray();
        middle[middle.Length / 2] = middle[middle.Length / 2] == 'A' ? 'B' : 'A';
        segments[^2] = new string(middle);

        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(server, string.Join('.', segments))).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            Slug(),
            Ct,
            configure: builder => builder.Services.Configure<OpenIddictServerOptions>(
                options => options.AccessTokenLifetime = TimeSpan.FromSeconds(2)));

        var tokens = await FullFlowAsync(server);
        Assert.True(tokens.ExpiresIn <= 2, $"expires_in {tokens.ExpiresIn}");

        await Task.Delay(TimeSpan.FromSeconds(tokens.ExpiresIn + 2), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(server, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_token_outlives_the_host_that_issued_it()
    {
        // Two hosts on two ports share one public URL, as one deployment does across a restart; the
        // database is all they have in common, so the keys can only have come from there.
        const string publicUrl = "http://127.0.0.1:5999";
        var slug = Slug();
        TokenSet tokens;

        await using (var first = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct, publicUrl: publicUrl))
        {
            tokens = await FullFlowAsync(first, $"{publicUrl}/mcp");
            Assert.Equal(HttpStatusCode.OK, (await McpAsync(first, tokens.AccessToken)).StatusCode);
        }

        await using var second = await AppServer.StartAsync(fixture.ConnectionString, slug, Ct, publicUrl: publicUrl);

        await using var client = await second.ConnectAsync(tokens.AccessToken, Ct);
        Assert.Contains("work_brief", await ToolNamesAsync(client));

        var refreshed = await RefreshAsync(second.NewClient(), tokens.ClientId, tokens.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(second, refreshed.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Static_and_session_tokens_get_the_bare_401_they_always_got()
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);

        foreach (var wrong in new[] { "not-a-configured-token", "wss_not-a-minted-token" })
        {
            var response = await McpAsync(server, wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(response.Headers.WwwAuthenticate);
        }

        Assert.Equal(HttpStatusCode.OK, (await McpAsync(server, ReadServer.ControllerToken)).StatusCode);
    }

    [Fact]
    public async Task Authorize_without_a_dashboard_session_asks_for_one()
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var anonymous = server.NewClient();
        var clientId = await RegisterAsync(anonymous);

        var authorize = AuthorizeUrl(clientId, Pkce.Create(), $"{server.BaseAddress}/mcp");
        var response = await anonymous.GetAsync(authorize, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(authorize, SignInReturnUrl(response));

        // The API beside it still answers a missing cookie with the bare 401, never a redirect.
        var api = await anonymous.GetAsync("/api/v1/overview", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Null(api.Headers.Location);
    }

    /// <summary>The same-origin authorize URL a 302 to the sign-in page carries, decoded once.</summary>
    internal static string SignInReturnUrl(HttpResponseMessage response)
    {
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/connect/signin?", location, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(location["/connect/signin".Length..])["returnUrl"].ToString();
    }

    [Theory]
    [InlineData("https://client.example/app", true)]
    [InlineData("custom://client.example/app", false)]
    [InlineData("http://client.example/app", false)]
    [InlineData("http://127.0.0.1:9000/callback", true)]
    [InlineData("http://localhost/callback", true)]
    [InlineData("https://client.example/app#fragment", false)]
    [InlineData("https://user@client.example/app", false)]
    public async Task Registration_takes_https_and_loopback_http_callbacks_only(string redirect, bool accepted)
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var response = await server.NewClient().PostAsync(
            "/connect/register",
            AppServer.Json(new { client_name = "test", redirect_uris = new[] { redirect } }),
            Ct);

        Assert.Equal(accepted ? HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Slug() => $"oauth-{Guid.CreateVersion7():n}";

    private async Task<TokenSet> FullFlowAsync(AppServer server, string? resource = null)
    {
        var anonymous = server.NewClient();
        var clientId = await RegisterAsync(anonymous);
        var browser = await server.SignInAsync(Ct);
        var pkce = Pkce.Create();
        var code = await AuthorizeForCodeAsync(browser, clientId, pkce, resource ?? $"{server.BaseAddress}/mcp");
        return await ExchangeAsync(anonymous, clientId, code, pkce);
    }

    internal static async Task<string> RegisterAsync(HttpClient client)
    {
        var response = await client.PostAsync(
            "/connect/register",
            AppServer.Json(new { client_name = "OAuth tests", redirect_uris = new[] { Callback } }),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal("none", body.GetProperty("token_endpoint_auth_method").GetString());
        return body.GetProperty("client_id").GetString()!;
    }

    internal static string AuthorizeUrl(string clientId, Pkce pkce, string resource, string redirectUri = Callback)
        => QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "mcp offline_access",
            ["state"] = "state-1",
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = resource,
        });

    internal static async Task<string> AuthorizeForCodeAsync(
        HttpClient browser,
        string clientId,
        Pkce pkce,
        string resource,
        string redirectUri = Callback)
    {
        var response = await browser.GetAsync(AuthorizeUrl(clientId, pkce, resource, redirectUri), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // A first authorize for this client asks; answer as the consent page does. SignInConsentTests proves
        // the ask happens, so this cannot hide a skipped consent.
        if (response.Headers.Location!.OriginalString.StartsWith("/connect/consent?", StringComparison.Ordinal))
        {
            response = await AnswerConsentAsync(browser, response.Headers.Location!, "granted");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        var location = response.Headers.Location!;
        Assert.StartsWith(redirectUri, location.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.False(query.ContainsKey("error"), $"authorize failed: {location}");
        Assert.Equal("state-1", query["state"].ToString());
        return query["code"].ToString();
    }

    /// <summary>
    /// What <c>frontend/src/Connect.tsx</c> submits: every parameter of the consent page's own query as a form
    /// field, plus the button pressed.
    /// </summary>
    internal static Task<HttpResponseMessage> AnswerConsentAsync(HttpClient browser, Uri consent, string answer)
    {
        var query = consent.OriginalString[consent.OriginalString.IndexOf('?', StringComparison.Ordinal)..];
        var fields = QueryHelpers.ParseQuery(query)
            .SelectMany(field => field.Value.Select(value => KeyValuePair.Create(field.Key, value ?? string.Empty)))
            .Append(KeyValuePair.Create("consent", answer));

        return browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields), Ct);
    }

    internal static async Task<TokenSet> ExchangeAsync(
        HttpClient client,
        string clientId,
        string code,
        Pkce pkce,
        string redirectUri = Callback)
    {
        var response = await TokenAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = pkce.Verifier,
        });

        return await TokensAsync(response, clientId);
    }

    private static async Task<TokenSet> RefreshAsync(HttpClient client, string clientId, string refreshToken)
        => await TokensAsync(
            await TokenAsync(client, new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
            }),
            clientId);

    private static async Task<TokenSet> TokensAsync(HttpResponseMessage response, string clientId)
    {
        var body = await ReadAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"token endpoint → {(int)response.StatusCode}: {body}");

        return new TokenSet(
            clientId,
            body.GetProperty("access_token").GetString()!,
            body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            body.GetProperty("expires_in").GetInt32());
    }

    private static Task<HttpResponseMessage> TokenAsync(HttpClient client, Dictionary<string, string> form)
        => client.PostAsync("/connect/token", new FormUrlEncodedContent(form), Ct);

    private static async Task RevokeAsync(HttpClient client, string clientId, string token)
    {
        var response = await client.PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["client_id"] = clientId }),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal static async Task<HttpResponseMessage> McpAsync(AppServer server, string token)
    {
        var request = AppServer.McpInitialize();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await server.NewClient().SendAsync(request, Ct);
    }

    private static async Task<List<string>> ToolNamesAsync(ModelContextProtocol.Client.McpClient client)
        => (await client.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).Order().ToList();

    private static string ResourceMetadataUrl(HttpResponseMessage response)
    {
        var header = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", header.Scheme);

        const string key = "resource_metadata=\"";
        var start = header.Parameter!.IndexOf(key, StringComparison.Ordinal) + key.Length;
        return header.Parameter[start..header.Parameter.IndexOf('"', start)];
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static string[] Strings(JsonElement element, string property)
        => element.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    internal sealed record TokenSet(string ClientId, string AccessToken, string? RefreshToken, int ExpiresIn);

    internal sealed record Pkce(string Verifier, string Challenge)
    {
        public static Pkce Create()
        {
            var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            return new Pkce(verifier, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
    }
}
