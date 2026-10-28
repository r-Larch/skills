using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using OpenIddict.Abstractions;
using WorkState.Api.App.V1;
using WorkState.Api.Auth.OAuth;
using WorkState.Core.Domain;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// Foreman's sign-in: an OAuth token for the second resource, <c>&lt;origin&gt;/api</c>, on the read API, the
/// stream and the agent-session mint — and the refusals that keep the two surfaces apart: audience per
/// surface, scope per route, and a <c>wss_</c> session that ends with the authorization it was minted under.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ApiOAuthTests(PostgresFixture fixture)
{
    private const string Callback = "http://127.0.0.1:7777/callback";
    private const string Foreman = "workstate.read workstate.sessions offline_access";

    [Fact]
    public async Task Foreman_reads_streams_mints_and_refreshes_with_an_api_token()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var tokens = await SignInAsync(server, Api(server), Foreman);

        var overview = await GetAsync(server, tokens.AccessToken, "/api/v1/overview");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Equal(ApiV1.Version, Assert.Single(overview.Headers.GetValues(ApiV1.Header)));

        using (var stream = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var client = Bearer(server, tokens.AccessToken);
            using var opened = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, stream.Token);
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            Assert.Equal("text/event-stream", opened.Content.Headers.ContentType?.MediaType);
            await stream.CancelAsync();
        }

        // The mint answers as it does for a static controller token: same request, same response.
        var (status, body) = await MintAsync(server, tokens.AccessToken, new { name = "fm-worker", role = "worker", launchedBy = "foreman" });
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("worker", body.GetProperty("role").GetString());
        Assert.Equal("fm-worker", body.GetProperty("name").GetString());
        var minted = body.GetProperty("token").GetString()!;
        Assert.StartsWith("wss_", minted, StringComparison.Ordinal);

        // The cap is the user's role, controller.
        Assert.Equal(HttpStatusCode.Created, (await MintAsync(server, tokens.AccessToken, new { name = "fm-controller", role = "controller" })).Status);

        // The wss_ token is the minted role on /mcp, nothing more.
        await using (var viaMinted = await server.ConnectAsync(minted, Ct))
        await using (var worker = await server.ConnectAsync(ReadServer.WorkerToken, Ct))
        {
            Assert.Equal(await ToolNamesAsync(worker), await ToolNamesAsync(viaMinted));
        }

        await using (var db = fixture.NewContext())
        {
            var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId && s.Name == "fm-worker", Ct);
            Assert.Equal(ActorRole.Worker, row.GrantedRole);
            Assert.Equal(AppServer.Username, row.MintedBySubject);
            Assert.False(string.IsNullOrEmpty(row.MintedByAuthorizationId));
            Assert.Null(row.MintedBySessionId);
            Assert.Equal("foreman", row.LaunchedBy);
        }

        var refreshed = await RefreshAsync(server, tokens);
        Assert.NotEqual(tokens.AccessToken, refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(server, refreshed.AccessToken, "/api/v1/overview")).StatusCode);
    }

    [Fact]
    public async Task A_static_mint_names_no_user_and_no_authorization()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        Assert.Equal(HttpStatusCode.Created, (await MintAsync(server, ReadServer.ControllerToken, new { name = "static-mint", role = "worker" })).Status);

        await using var db = fixture.NewContext();
        var row = await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.WorkspaceId && s.Name == "static-mint", Ct);
        Assert.Null(row.MintedBySubject);
        Assert.Null(row.MintedByAuthorizationId);
    }

    [Fact]
    public async Task Each_surface_refuses_the_other_surfaces_token()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var mcp = await SignInAsync(server, $"{server.BaseAddress}/mcp", "mcp offline_access");
        var api = await SignInAsync(server, Api(server), Foreman);

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(server, mcp.AccessToken, "/api/v1/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MintAsync(server, mcp.AccessToken, new { name = "x", role = "worker" })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, api.AccessToken)).StatusCode);

        // Each token is admitted on its own surface, so the refusals are about the audience alone.
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, mcp.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(server, api.AccessToken, "/api/v1/overview")).StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_routes_scope_is_forbidden()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        var readOnly = await SignInAsync(server, Api(server), "workstate.read offline_access");
        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(server, readOnly.AccessToken, new { name = "ro", role = "worker" })).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(server, readOnly.AccessToken, "/api/v1/overview")).StatusCode);

        var sessionsOnly = await SignInAsync(server, Api(server), "workstate.sessions");
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(server, sessionsOnly.AccessToken, "/api/v1/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(server, sessionsOnly.AccessToken, "/api/stream")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await MintAsync(server, sessionsOnly.AccessToken, new { name = "so", role = "worker" })).Status);
    }

    [Fact]
    public async Task The_api_resource_is_advertised_and_named_by_its_challenge()
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var anonymous = server.NewClient();

        var resource = await AppServer.ReadJsonAsync(anonymous, "/.well-known/oauth-protected-resource/api", Ct);
        Assert.Equal(Api(server), resource.GetProperty("resource").GetString());
        Assert.Equal([server.BaseAddress], Strings(resource, "authorization_servers"));
        Assert.Equal(["workstate.read", "workstate.sessions", "offline_access"], Strings(resource, "scopes_supported"));

        var mcp = await AppServer.ReadJsonAsync(anonymous, "/.well-known/oauth-protected-resource/mcp", Ct);
        Assert.Equal($"{server.BaseAddress}/mcp", mcp.GetProperty("resource").GetString());
        Assert.Equal(["mcp", "offline_access"], Strings(mcp, "scopes_supported"));

        var discovery = await AppServer.ReadJsonAsync(anonymous, "/.well-known/oauth-authorization-server", Ct);
        Assert.Contains("workstate.read", Strings(discovery, "scopes_supported"));
        Assert.Contains("workstate.sessions", Strings(discovery, "scopes_supported"));
        Assert.Contains("mcp", Strings(discovery, "scopes_supported"));

        var expected = $"Bearer resource_metadata=\"{server.BaseAddress}/.well-known/oauth-protected-resource/api\"";
        foreach (var request in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Get, "/api/v1/overview"),
                     new HttpRequestMessage(HttpMethod.Get, "/api/stream"),
                     new HttpRequestMessage(HttpMethod.Post, "/api/agent-sessions") { Content = AppServer.Json(new { name = "x", role = "worker" }) },
                 })
        {
            var response = await anonymous.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(expected, Assert.Single(response.Headers.GetValues("WWW-Authenticate")));
            Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
        }

        // A wrong static token still gets the bare 401.
        var wrong = Bearer(server, "not-a-configured-token");
        var bare = await wrong.GetAsync("/api/v1/overview", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Empty(bare.Headers.WwwAuthenticate);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("unknown")]
    public async Task Authorize_refuses_a_resource_set_it_cannot_issue_one_token_for(string which)
    {
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, Slug(), Ct);
        var clientId = await OAuthTests.RegisterAsync(server.NewClient());
        var browser = await server.SignInAsync(Ct);

        string[] resources = which == "both"
            ? [$"{server.BaseAddress}/mcp", Api(server)]
            : ["https://elsewhere.example/api"];

        // OpenIddict answers this one itself, before it has validated the redirect URI: a 400, never a code.
        var response = await browser.GetAsync(AuthorizeUrl(clientId, OAuthTests.Pkce.Create(), resources, Foreman), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("invalid_target", body, StringComparison.Ordinal);
        Assert.Contains(which == "both" ? "not both" : "is not served here", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_out_ends_the_sessions_the_app_minted_and_no_others()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var tokens = await SignInAsync(server, Api(server), Foreman);

        var viaOAuth = Token(await MintAsync(server, tokens.AccessToken, new { name = "fm-a", role = "worker" }));
        var viaStatic = Token(await MintAsync(server, ReadServer.ControllerToken, new { name = "static-a", role = "worker" }));
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, viaOAuth)).StatusCode);

        // Revoking the access token alone is not a sign-out: the authorization stands.
        await RevokeAsync(server, tokens.ClientId, tokens.AccessToken);
        Assert.Equal("valid", await AuthorizationStatusAsync(world, "fm-a"));
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, viaOAuth)).StatusCode);

        // Revoking the refresh token is: the authorization, and the session minted under it, end now.
        await RevokeAsync(server, tokens.ClientId, tokens.RefreshToken!);
        Assert.Equal("revoked", await AuthorizationStatusAsync(world, "fm-a"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, viaOAuth)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, viaStatic)).StatusCode);
    }

    [Fact]
    public async Task A_pruned_authorization_ends_its_sessions_too()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var tokens = await SignInAsync(server, Api(server), Foreman);

        var minted = Token(await MintAsync(server, tokens.AccessToken, new { name = "fm-pruned", role = "worker" }));
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, minted)).StatusCode);

        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM openiddict_token t USING agent_session s
                 WHERE s.workspace_id = @workspace AND s.name = 'fm-pruned' AND t.authorization_id = s.minted_by_authorization_id;
                DELETE FROM openiddict_authorization a USING agent_session s
                 WHERE s.workspace_id = @workspace AND s.name = 'fm-pruned' AND a.id = s.minted_by_authorization_id;
                """,
                new { workspace = world.WorkspaceId });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, minted)).StatusCode);
    }

    [Fact]
    public async Task A_metadata_client_registered_before_the_api_scopes_gains_them()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var clientId = $"https://foreman.example/{Guid.CreateVersion7():n}/client.json";
        var document = $$"""
            {"client_id":"{{clientId}}","client_name":"Foreman","redirect_uris":["http://127.0.0.1/callback"]}
            """;

        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString,
            world.Slug,
            Ct,
            configure: builder => builder.Services.PostConfigure<HttpClientFactoryOptions>(
                ClientIdMetadata.HttpClientName,
                options => options.HttpMessageHandlerBuilderActions.Add(
                    handler => handler.PrimaryHandler = new Document(clientId, document))));

        // The row an older server wrote: the same client, without the two scopes.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var old = OAuthEndpoints.PublicClient(clientId, "Foreman");
            old.Permissions.Remove(OpenIddictConstants.Permissions.Prefixes.Scope + OAuthServer.ReadScope);
            old.Permissions.Remove(OpenIddictConstants.Permissions.Prefixes.Scope + OAuthServer.SessionsScope);
            old.RedirectUris.Add(new Uri("http://127.0.0.1/callback"));
            await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(old, Ct);
        }

        // RFC 8252: the document declares the port-less loopback callback; Foreman uses whatever port it got.
        const string redirect = "http://127.0.0.1:53117/callback";
        var browser = await server.SignInAsync(Ct);
        var pkce = OAuthTests.Pkce.Create();
        var code = await AuthorizeForCodeAsync(browser, clientId, pkce, [Api(server)], Foreman, redirect);
        var tokens = await OAuthTests.ExchangeAsync(server.NewClient(), clientId, code, pkce, redirect);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(server, tokens.AccessToken, "/api/v1/overview")).StatusCode);
    }

    private async Task<string?> AuthorizationStatusAsync(WriteWorld world, string session)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<string?>(
            """
            SELECT a.status FROM openiddict_authorization a
              JOIN agent_session s ON a.id = s.minted_by_authorization_id
             WHERE s.workspace_id = @workspace AND s.name = @session
            """,
            new { workspace = world.WorkspaceId, session });
    }

    private static string Slug() => $"api-oauth-{Guid.CreateVersion7():n}";

    private static string Api(AppServer server) => $"{server.BaseAddress}/api";

    private static async Task<OAuthTests.TokenSet> SignInAsync(AppServer server, string resource, string scope)
    {
        var anonymous = server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous);
        var browser = await server.SignInAsync(Ct);
        var pkce = OAuthTests.Pkce.Create();
        var code = await AuthorizeForCodeAsync(browser, clientId, pkce, [resource], scope);
        return await OAuthTests.ExchangeAsync(anonymous, clientId, code, pkce);
    }

    private static string AuthorizeUrl(string clientId, OAuthTests.Pkce pkce, string[] resources, string scope, string redirectUri = Callback)
        => "/connect/authorize" + QueryString.Create(
            new Dictionary<string, string>
                {
                    ["response_type"] = "code",
                    ["client_id"] = clientId,
                    ["redirect_uri"] = redirectUri,
                    ["scope"] = scope,
                    ["state"] = "state-1",
                    ["code_challenge"] = pkce.Challenge,
                    ["code_challenge_method"] = "S256",
                }
                .Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value))
                .Concat(resources.Select(resource => KeyValuePair.Create("resource", (string?)resource))));

    private static async Task<string> AuthorizeForCodeAsync(
        HttpClient browser,
        string clientId,
        OAuthTests.Pkce pkce,
        string[] resources,
        string scope,
        string redirectUri = Callback)
    {
        var response = await browser.GetAsync(AuthorizeUrl(clientId, pkce, resources, scope, redirectUri), Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        if (response.Headers.Location!.OriginalString.StartsWith("/connect/consent?", StringComparison.Ordinal))
        {
            response = await OAuthTests.AnswerConsentAsync(browser, response.Headers.Location!, "granted");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        var location = response.Headers.Location!;
        Assert.StartsWith(redirectUri, location.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.False(query.TryGetValue("error", out var error), $"authorize failed: {error}");
        return query["code"].ToString();
    }

    private static async Task<OAuthTests.TokenSet> RefreshAsync(AppServer server, OAuthTests.TokenSet tokens)
    {
        var response = await server.NewClient().PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken!,
                ["client_id"] = tokens.ClientId,
            }),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        return tokens with
        {
            AccessToken = body.GetProperty("access_token").GetString()!,
            RefreshToken = body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : tokens.RefreshToken,
        };
    }

    private static async Task RevokeAsync(AppServer server, string clientId, string token)
    {
        var response = await server.NewClient().PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["client_id"] = clientId }),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpClient Bearer(AppServer server, string token)
    {
        var client = server.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> GetAsync(AppServer server, string token, string path)
        => Bearer(server, token).GetAsync(path, HttpCompletionOption.ResponseHeadersRead, Ct);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> MintAsync(AppServer server, string token, object body)
    {
        var response = await Bearer(server, token).PostAsync("/api/agent-sessions", AppServer.Json(body), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Token((HttpStatusCode Status, JsonElement Body) minted)
    {
        Assert.Equal(HttpStatusCode.Created, minted.Status);
        return minted.Body.GetProperty("token").GetString()!;
    }

    private static async Task<List<string>> ToolNamesAsync(ModelContextProtocol.Client.McpClient client)
        => (await client.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).Order().ToList();

    private static string[] Strings(JsonElement element, string property)
        => element.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    /// <summary>Serves one document at one URL; anything else is a 404.</summary>
    private sealed class Document(string url, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri?.OriginalString == url
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
