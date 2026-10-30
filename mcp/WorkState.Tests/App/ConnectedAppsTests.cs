using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkState.Core.Domain;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.App.AdminApi;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// m65, doc 13: <c>/api/session/apps</c> lists the cookie user's own OAuth authorizations in the cookie's tenant, and
/// Revoke ends one — the authorization, its tokens, and the <c>wss_</c> sessions minted with them, whose claims are
/// released. Anyone else's, another tenant's, an unknown or an already revoked id is not found and changes nothing.
/// U1 is <see cref="TenantWorld.UserAB"/>, a member of A and B; U2 is <see cref="TenantWorld.UserA"/>, of A only.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ConnectedAppsTests(PostgresFixture fixture)
{
    private const string Scopes = "mcp workstate.read workstate.sessions offline_access";

    [Fact]
    public async Task ConnectedApps_lists_the_users_app_with_its_live_session_and_no_secret()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CookieAsync(world.UserAB);
        var app = await ConnectAsync(world, u1);
        var minted = await MintAndClaimAsync(world, app);

        var listed = await ListAsync(u1);
        var row = Assert.Single(listed.Apps);
        Assert.Equal(app.ClientId, row.GetProperty("clientId").GetString());
        Assert.Equal("OAuth tests", row.GetProperty("name").GetString());
        Assert.Equal(Scopes.Split(' ').Order(), row.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).Order());
        Assert.Equal(JsonValueKind.String, row.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, row.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(1, row.GetProperty("sessions").GetInt32());

        foreach (var secret in new[] { app.Api.AccessToken, app.Api.RefreshToken!, app.Mcp.AccessToken, app.Mcp.RefreshToken!, minted })
        {
            Assert.DoesNotContain(secret, listed.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConnectedApps_revoke_ends_the_tokens_the_refresh_and_the_minted_session_and_releases_its_claim()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CookieAsync(world.UserAB);
        var app = await ConnectAsync(world, u1);
        var minted = await MintAndClaimAsync(world, app);
        var id = Assert.Single((await ListAsync(u1)).Apps).GetProperty("id").GetString()!;

        var revoked = await JsonAsync(await SendAsync(u1, HttpMethod.Post, $"/api/session/apps/{id}/revoke"), HttpStatusCode.OK);
        Assert.True(revoked.GetProperty("tokensRevoked").GetInt64() > 0, revoked.ToString());
        Assert.Equal(1, revoked.GetProperty("sessionsRevoked").GetInt32());

        foreach (var tokens in new[] { app.Api, app.Mcp })
        {
            var refresh = await RefreshAsync(world, tokens);
            Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
            Assert.Equal("invalid_grant", (await OAuthTests.ReadAsync(refresh)).GetProperty("error").GetString());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, app.Mcp.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, minted)).StatusCode);

        var session = await SessionNamedAsync(world, "apps-worker");
        Assert.Equal("revoked", (await SessionsListedAsync(u1)).Single(s => s.Id == session).Token);

        await using (var db = fixture.NewContext())
        {
            var claim = await db.Claims.SingleAsync(c => c.ItemId == world.A.TaskId, Ct);
            Assert.NotNull(claim.ReleasedAt);
            Assert.Equal("revoked", claim.ReleaseReason);

            var journal = await db.Events.SingleAsync(e => e.ItemId == world.A.TaskId && e.Kind == "claim_revoked", Ct);
            Assert.Equal(world.UserAB, journal.Actor);
            Assert.Equal(ActorRole.Human, journal.Role);
        }

        Assert.Equal(ItemStatus.Ready, await world.StatusAsync(world.A.TaskId));
        Assert.Empty((await ListAsync(u1)).Apps);

        // Already revoked is not found, like any id that is not one of the caller's.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(u1, HttpMethod.Post, $"/api/session/apps/{id}/revoke")).StatusCode);
    }

    [Fact]
    public async Task ConnectedApps_another_user_of_the_tenant_neither_sees_nor_revokes_the_app()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CookieAsync(world.UserAB);
        var app = await ConnectAsync(world, u1);
        var id = Assert.Single((await ListAsync(u1)).Apps).GetProperty("id").GetString()!;

        var u2 = await world.CookieAsync(world.UserA);
        await ConnectAsync(world, u2);
        var theirs = Assert.Single((await ListAsync(u2)).Apps);
        Assert.NotEqual(id, theirs.GetProperty("id").GetString());

        // U2, a member of B only, and an id that is no authorization at all: the same 404, with the same words.
        var unknown = Guid.CreateVersion7().ToString();
        var outsider = await world.CookieAsync(world.UserB);
        var refused = await SendAsync(u2, HttpMethod.Post, $"/api/session/apps/{id}/revoke");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(outsider, HttpMethod.Post, $"/api/session/apps/{id}/revoke")).StatusCode);
        var missing = await SendAsync(u2, HttpMethod.Post, $"/api/session/apps/{unknown}/revoke");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal((await ErrorAsync(missing)).Replace(unknown, "<id>"), (await ErrorAsync(refused)).Replace(id, "<id>"));

        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, app.Mcp.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(world, app.Mcp)).StatusCode);
        Assert.Equal(id, Assert.Single((await ListAsync(u1)).Apps).GetProperty("id").GetString());
    }

    [Fact]
    public async Task ConnectedApps_the_same_client_in_two_tenants_is_listed_and_revoked_per_tenant()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CookieAsync(world.UserAB);
        var clientId = await OAuthTests.RegisterAsync(server.NewClient());

        var inA = await ConnectAsync(world, u1, clientId);
        var idA = Assert.Single((await ListAsync(u1)).Apps).GetProperty("id").GetString()!;

        await SwitchAsync(u1, world.B.WorkspaceId);
        var inB = await ConnectAsync(world, u1, clientId);
        var idB = Assert.Single((await ListAsync(u1)).Apps).GetProperty("id").GetString()!;
        Assert.NotEqual(idA, idB);

        // From B, A's authorization is not one of this tenant's.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(u1, HttpMethod.Post, $"/api/session/apps/{idA}/revoke")).StatusCode);

        await SwitchAsync(u1, world.A.WorkspaceId);
        await JsonAsync(await SendAsync(u1, HttpMethod.Post, $"/api/session/apps/{idA}/revoke"), HttpStatusCode.OK);
        Assert.Empty((await ListAsync(u1)).Apps);
        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, inA.Mcp.AccessToken)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, inB.Mcp.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(world, inB.Mcp)).StatusCode);
        await SwitchAsync(u1, world.B.WorkspaceId);
        Assert.Equal(idB, Assert.Single((await ListAsync(u1)).Apps).GetProperty("id").GetString());
    }

    [Fact]
    public async Task ConnectedApps_needs_the_cookie_and_refuses_a_bearer_as_the_password_change_does()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CookieAsync(world.UserAB);
        var app = await ConnectAsync(world, u1);
        var id = Assert.Single((await ListAsync(u1)).Apps).GetProperty("id").GetString()!;

        var anonymous = server.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/session/apps", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, HttpMethod.Post, $"/api/session/apps/{id}/revoke")).StatusCode);

        foreach (var bearer in new[] { world.Bearer(ReadServer.ControllerToken), world.Bearer(app.Api.AccessToken) })
        {
            var password = (await SendAsync(bearer, HttpMethod.Post, "/api/session/password", new { current = "x", next = "y" })).StatusCode;
            Assert.True(password is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"password → {(int)password}");
            Assert.Equal(password, (await bearer.GetAsync("/api/session/apps", Ct)).StatusCode);
            Assert.Equal(password, (await SendAsync(bearer, HttpMethod.Post, $"/api/session/apps/{id}/revoke")).StatusCode);
        }

        // A write without the write header is refused as every dashboard write is.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(u1, HttpMethod.Post, $"/api/session/apps/{id}/revoke", header: false)).StatusCode);
        Assert.Single((await ListAsync(u1)).Apps);
    }

    [Fact]
    public async Task ConnectedApps_a_client_signing_out_at_connect_revoke_still_ends_its_authorization()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var u1 = await world.CookieAsync(world.UserAB);
        var app = await ConnectAsync(world, u1);
        var minted = await TenantWorld.MintAsync(world.Bearer(app.Api.AccessToken), "apps-signout", "worker");
        Assert.Single((await ListAsync(u1)).Apps);

        var signOut = await server.NewClient().PostAsync(
            "/connect/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = app.Mcp.RefreshToken!, ["client_id"] = app.ClientId }),
            Ct);
        Assert.Equal(HttpStatusCode.OK, signOut.StatusCode);

        Assert.Empty((await ListAsync(u1)).Apps);
        Assert.Equal(HttpStatusCode.BadRequest, (await RefreshAsync(world, app.Api)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, app.Mcp.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(server, minted)).StatusCode);
    }

    /// <summary>One client, one approval: a token for <c>/api</c> on consent, then one for <c>/mcp</c> the approval remembers.</summary>
    private static async Task<ConnectedApp> ConnectAsync(TenantWorld world, HttpClient browser, string? clientId = null)
    {
        var anonymous = world.Server.NewClient();
        clientId ??= await OAuthTests.RegisterAsync(anonymous);

        var pkce = OAuthTests.Pkce.Create();
        var code = await OAuthTests.AuthorizeForCodeAsync(browser, clientId, pkce, $"{world.Server.BaseAddress}/api", scope: Scopes);
        var api = await OAuthTests.ExchangeAsync(anonymous, clientId, code, pkce);

        pkce = OAuthTests.Pkce.Create();
        var remembered = await browser.GetAsync(OAuthTests.AuthorizeUrl(clientId, pkce, $"{world.Server.BaseAddress}/mcp"), Ct);
        Assert.Equal(HttpStatusCode.Redirect, remembered.StatusCode);
        Assert.True(remembered.Headers.Location!.IsAbsoluteUri, $"expected the client's callback, got {remembered.Headers.Location}");
        var mcpCode = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(remembered.Headers.Location.Query)["code"].ToString();
        var mcp = await OAuthTests.ExchangeAsync(anonymous, clientId, mcpCode, pkce);

        return new ConnectedApp(clientId, api, mcp);
    }

    /// <summary>A <c>wss_</c> session minted with the app's <c>/api</c> token, holding the claim on A's task.</summary>
    private static async Task<string> MintAndClaimAsync(TenantWorld world, ConnectedApp app)
    {
        var minted = await TenantWorld.MintAsync(world.Bearer(app.Api.AccessToken), "apps-worker", "orchestrator");
        await using var mcp = await world.McpAsync(minted);
        var (text, refused) = await ReadServer.CallEitherAsync(
            mcp,
            "work_status",
            new Dictionary<string, object?> { ["ref"] = "run-a/1.1", ["to"] = "in_progress", ["tree"] = $"p:/wt-{Guid.NewGuid():n}"[..18] },
            Ct);
        Assert.False(refused, text);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync(world.A.TaskId));
        return minted;
    }

    private static async Task<(string Text, IReadOnlyList<JsonElement> Apps)> ListAsync(HttpClient cookie)
    {
        var response = await cookie.GetAsync("/api/session/apps", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"apps → {(int)response.StatusCode}: {text}");
        return (text, [.. JsonDocument.Parse(text).RootElement.Clone().GetProperty("apps").EnumerateArray()]);
    }

    private static Task<HttpResponseMessage> RefreshAsync(TenantWorld world, OAuthTests.TokenSet tokens)
        => OAuthTests.TokenAsync(world.Server.NewClient(), new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.RefreshToken!,
            ["client_id"] = tokens.ClientId,
        });

    private static async Task<List<(Guid Id, string Token)>> SessionsListedAsync(HttpClient cookie)
    {
        var body = await JsonAsync(await cookie.GetAsync("/api/v1/sessions", Ct), HttpStatusCode.OK);
        return [.. body.GetProperty("sessions").EnumerateArray()
            .Select(s => (s.GetProperty("session").GetProperty("id").GetGuid(), s.GetProperty("token").GetString()!))];
    }

    private async Task<Guid> SessionNamedAsync(TenantWorld world, string name)
    {
        await using var db = fixture.NewContext();
        return (await db.AgentSessions.SingleAsync(s => s.WorkspaceId == world.A.WorkspaceId && s.Name == name, Ct)).Id;
    }

    private static async Task SwitchAsync(HttpClient cookie, Guid tenant)
        => Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(cookie, HttpMethod.Post, "/api/session/tenant", new { id = tenant })).StatusCode);

    private sealed record ConnectedApp(string ClientId, OAuthTests.TokenSet Api, OAuthTests.TokenSet Mcp);
}
