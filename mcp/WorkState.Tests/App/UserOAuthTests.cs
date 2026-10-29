using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WorkState.Api.App;
using WorkState.Api.Auth.OAuth;
using WorkState.Mcp;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// m51: an OAuth token is one user's in one tenant — the tenant their dashboard session was in when they
/// approved — and stays so across refreshes; an approval in one tenant is never a token for another; losing the
/// tenant or the account ends the token, its refresh and the sessions it minted; a user's session names are
/// theirs; and a token issued before any of this still refreshes, into the new shape. Every check runs in
/// <see cref="TenantWorld"/>, where A and B share every ref.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class UserOAuthTests(PostgresFixture fixture)
{
    private const string Mcp = "mcp offline_access";
    private const string Api = "workstate.read workstate.sessions offline_access";

    [Fact]
    public async Task A_token_names_its_user_by_id_and_acts_in_the_tenant_it_was_approved_in_only()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var user = await world.UserIdAsync(world.UserAB);
        var browser = await world.CookieAsync(world.UserAB);

        var tokens = await SignInAsync(world, browser, McpResource(world), Mcp);
        await AssertTokenAsync(world, tokens.AccessToken, user, world.A.WorkspaceId);

        await using (var oauth = await world.McpAsync(tokens.AccessToken))
        await using (var controller = await world.McpAsync(ReadServer.ControllerToken))
        {
            Assert.Equal(await ToolNamesAsync(controller), await ToolNamesAsync(oauth));
        }

        await AssertActsInAsync(world, tokens.AccessToken, TenantWorld.TitleA, TenantWorld.TitleB, TenantWorld.TitleOnlyInB);

        // The cookie moving to B moves nothing the token does, and a refresh keeps its user and its tenant.
        await SwitchAsync(browser, world.B.WorkspaceId);
        await AssertActsInAsync(world, tokens.AccessToken, TenantWorld.TitleA, TenantWorld.TitleB, TenantWorld.TitleOnlyInB);

        var refreshed = await OAuthTests.RefreshAsync(server.NewClient(), tokens.ClientId, tokens.RefreshToken!);
        await AssertTokenAsync(world, refreshed.AccessToken, user, world.A.WorkspaceId);
        await AssertActsInAsync(world, refreshed.AccessToken, TenantWorld.TitleA, TenantWorld.TitleB, TenantWorld.TitleOnlyInB);

        // Signing in once more from B is a token for B, which sees B's task under the same ref and never A's.
        var inB = await SignInAsync(world, browser, McpResource(world), Mcp);
        await AssertTokenAsync(world, inB.AccessToken, user, world.B.WorkspaceId);
        await AssertActsInAsync(world, inB.AccessToken, TenantWorld.TitleB, TenantWorld.TitleA);
    }

    [Fact]
    public async Task An_approval_in_tenant_A_is_never_a_token_for_tenant_B()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var user = await world.UserIdAsync(world.UserAB);
        var browser = await world.CookieAsync(world.UserAB);
        var anonymous = server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous);

        var pkce = OAuthTests.Pkce.Create();
        var consentInA = ConsentLocation(await browser.GetAsync(Authorize(world, clientId, pkce), Ct));
        var inA = await OAuthTests.ExchangeAsync(
            anonymous, clientId, CodeOf(await OAuthTests.AnswerConsentAsync(browser, consentInA, "granted")), pkce);
        await AssertTokenAsync(world, inA.AccessToken, user, world.A.WorkspaceId);

        // Remembered in A: the next authorize there goes straight back to the client.
        CodeOf(await browser.GetAsync(Authorize(world, clientId, OAuthTests.Pkce.Create()), Ct));

        // In B that approval is none: the page asks again, prompt=none says so, and the answer is a token for B.
        await SwitchAsync(browser, world.B.WorkspaceId);
        pkce = OAuthTests.Pkce.Create();
        var consentInB = ConsentLocation(await browser.GetAsync(Authorize(world, clientId, pkce), Ct));
        Assert.Equal("consent_required", ErrorOf(await browser.GetAsync(Authorize(world, clientId, OAuthTests.Pkce.Create()) + "&prompt=none", Ct)));

        var inB = await OAuthTests.ExchangeAsync(
            anonymous, clientId, CodeOf(await OAuthTests.AnswerConsentAsync(browser, consentInB, "granted")), pkce);
        await AssertTokenAsync(world, inB.AccessToken, user, world.B.WorkspaceId);
        await AssertActsInAsync(world, inB.AccessToken, TenantWorld.TitleB, TenantWorld.TitleA);

        // Each approval stays its own tenant's: back in A, A's still answers without a page.
        await SwitchAsync(browser, world.A.WorkspaceId);
        pkce = OAuthTests.Pkce.Create();
        var backInA = await OAuthTests.ExchangeAsync(anonymous, clientId, CodeOf(await browser.GetAsync(Authorize(world, clientId, pkce), Ct)), pkce);
        await AssertTokenAsync(world, backInA.AccessToken, user, world.A.WorkspaceId);

        Assert.Equal(
            new[] { world.A.WorkspaceId, world.B.WorkspaceId }.Order(),
            (await ApprovalsAsync(user, clientId)).Order());
    }

    [Fact]
    public async Task An_approval_posted_from_a_page_that_showed_another_tenant_is_asked_for_again()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var user = await world.UserIdAsync(world.UserAB);
        var browser = await world.CookieAsync(world.UserAB);
        var anonymous = server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous);

        // The page showed A; the session moved to B in another tab before the user approved.
        var pkce = OAuthTests.Pkce.Create();
        var consent = ConsentLocation(await browser.GetAsync(Authorize(world, clientId, pkce), Ct));
        await SwitchAsync(browser, world.B.WorkspaceId);

        var stale = ConsentLocation(await AnswerAsync(browser, consent, "granted", world.A.WorkspaceId));
        Assert.Equal(consent.OriginalString, stale.OriginalString);
        Assert.Empty(await ApprovalsAsync(user, clientId));

        // Answered for the tenant the page now shows, it is a token for that one.
        var tokens = await OAuthTests.ExchangeAsync(
            anonymous, clientId, CodeOf(await AnswerAsync(browser, stale, "granted", world.B.WorkspaceId)), pkce);
        await AssertTokenAsync(world, tokens.AccessToken, user, world.B.WorkspaceId);
        Assert.Equal([world.B.WorkspaceId], await ApprovalsAsync(user, clientId));
    }

    [Fact]
    public async Task A_removed_membership_ends_the_users_tokens_refreshes_and_sessions_in_that_tenant_only()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var browser = await world.CookieAsync(world.UserAB);

        var mcpInA = await SignInAsync(world, browser, McpResource(world), Mcp);
        var apiInA = await SignInAsync(world, browser, ApiResource(world), Api);
        var mintedInA = await TenantWorld.MintAsync(world.Bearer(apiInA.AccessToken), "ab-worker", "worker");
        await SwitchAsync(browser, world.B.WorkspaceId);
        var mcpInB = await SignInAsync(world, browser, McpResource(world), Mcp);

        foreach (var token in new[] { mcpInA.AccessToken, mintedInA, mcpInB.AccessToken })
        {
            Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, token)).StatusCode);
        }

        await world.LeaveAsync(world.UserAB, world.A.WorkspaceId);

        await AssertEndedAsync(world, mcpInA, apiInA, mintedInA, "may no longer enter the workspace");

        // B, where they are still a member, is untouched: the token works and refreshes.
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, mcpInB.AccessToken)).StatusCode);
        var refreshed = await OAuthTests.RefreshAsync(server.NewClient(), mcpInB.ClientId, mcpInB.RefreshToken!);
        await AssertActsInAsync(world, refreshed.AccessToken, TenantWorld.TitleB, TenantWorld.TitleA);
    }

    [Fact]
    public async Task A_disabled_user_loses_every_token_refresh_and_session_and_no_one_else_does()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var browser = await world.CookieAsync(world.UserA);

        var mcp = await SignInAsync(world, browser, McpResource(world), Mcp);
        var api = await SignInAsync(world, browser, ApiResource(world), Api);
        var minted = await TenantWorld.MintAsync(world.Bearer(api.AccessToken), "a-worker", "worker");
        var others = await SignInAsync(world, await world.CookieAsync(world.UserAB), McpResource(world), Mcp);
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, minted)).StatusCode);

        await world.DisableAsync(world.UserA);

        await AssertEndedAsync(world, mcp, api, minted, "no longer signs in");
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, others.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OAuthTests.McpAsync(server, ReadServer.ControllerToken)).StatusCode);
    }

    [Fact]
    public async Task Two_users_of_tenant_A_each_mint_worker_1_and_each_manages_only_their_own()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var userA = await world.UserIdAsync(world.UserA);
        var userAB = await world.UserIdAsync(world.UserAB);
        var a = (await SignInAsync(world, await world.CookieAsync(world.UserA), ApiResource(world), Api)).AccessToken;
        var abBrowser = await world.CookieAsync(world.UserAB);
        var ab = (await SignInAsync(world, abBrowser, ApiResource(world), Api)).AccessToken;

        var mine = SessionId(await MintAsync(world, a, new { name = "worker-1", role = "worker" }));
        var theirs = SessionId(await MintAsync(world, ab, new { name = "worker-1", role = "worker" }));
        Assert.NotEqual(mine, theirs);

        await using (var db = fixture.NewContext())
        {
            var rows = await db.AgentSessions.Where(s => s.WorkspaceId == world.A.WorkspaceId && s.Name == "worker-1").ToListAsync(Ct);
            Assert.Equal(new Guid?[] { userA, userAB }.Order(), rows.Select(s => s.UserId).Order());
        }

        // Within one user the name stays unique, and replace still lands on the same session.
        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(world, a, new { name = "worker-1", role = "worker" })).Status);
        Assert.Equal(mine, SessionId(await MintAsync(world, a, new { name = "worker-1", role = "worker", replace = true })));

        // The static tokens keep the one namespace per workspace they always shared.
        var shared = SessionId(await MintAsync(world, ReadServer.ControllerToken, new { name = "worker-1", role = "worker" }));
        Assert.DoesNotContain(shared, new[] { mine, theirs });
        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(world, ReadServer.ControllerToken, new { name = "worker-1", role = "worker" })).Status);

        // A session a user's token minted mints under that user: their names, their rule.
        var lead = Token(await MintAsync(world, a, new { name = "lead", role = "controller" }));
        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(world, lead, new { name = "worker-1", role = "worker" })).Status);
        var second = SessionId(await MintAsync(world, lead, new { name = "worker-2", role = "worker" }));
        await using (var db = fixture.NewContext())
        {
            Assert.Equal(userA, (await db.AgentSessions.SingleAsync(s => s.Id == second, Ct)).UserId);
        }

        // A member changes only what they minted; anyone else's session is not found (m62 C, controller decision).
        var refused = await world.Bearer(a).DeleteAsync($"/api/agent-sessions/{theirs}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(world, a, theirs)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Bearer(a).PostAsync($"/api/agent-sessions/{theirs}/release", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Bearer(a).DeleteAsync($"/api/agent-sessions/{shared}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(world, a, mine)).StatusCode);

        // UserAB's own session in B is no session of theirs in A: their A token finds nothing there.
        await SwitchAsync(abBrowser, world.B.WorkspaceId);
        var abInB = (await SignInAsync(world, abBrowser, ApiResource(world), Api)).AccessToken;
        var inB = SessionId(await MintAsync(world, abInB, new { name = "worker-1", role = "worker" }));
        Assert.Equal(HttpStatusCode.NotFound, (await world.Bearer(ab).DeleteAsync($"/api/agent-sessions/{inB}", Ct)).StatusCode);

        // An admin of the tenant changes any session in it.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync(
                "UPDATE tenant_membership SET role = 'admin' WHERE user_id = @userA AND workspace_id = @workspace;",
                new { userA, workspace = world.A.WorkspaceId });
        }

        Assert.Equal(HttpStatusCode.NoContent, (await world.Bearer(a).DeleteAsync($"/api/agent-sessions/{theirs}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await world.Bearer(a).DeleteAsync($"/api/agent-sessions/{shared}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_header_named_session_resolves_among_the_users_own_names_in_the_tokens_tenant()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var a = (await SignInAsync(world, await world.CookieAsync(world.UserA), ApiResource(world), Api)).AccessToken;
        var ab = (await SignInAsync(world, await world.CookieAsync(world.UserAB), ApiResource(world), Api)).AccessToken;
        var mine = SessionId(await MintAsync(world, a, new { name = "worker-1", role = "worker" }));
        SessionId(await MintAsync(world, ab, new { name = "worker-1", role = "worker" }));

        var mcp = await SignInAsync(world, await world.CookieAsync(world.UserA), McpResource(world), Mcp);
        const string body = "tenant user-oauth header note";
        await using (var named = await server.ConnectAsync(mcp.AccessToken, Ct, new Dictionary<string, string> { [CallerSession.AgentHeader] = "worker-1" }))
        {
            var note = await ReadServer.CallEitherAsync(
                named, "work_note", Args(("kind", "observation"), ("body", body), ("to", $"{TenantWorld.Project}/run-a/1.1")), Ct);
            Assert.False(note.Refused, note.Text);
        }

        await using var db = fixture.NewContext();
        var written = await db.Notes.SingleAsync(n => n.Body == body, Ct);
        Assert.Equal(world.A.WorkspaceId, written.WorkspaceId);
        Assert.Equal(mine, written.SessionId);
        Assert.Equal(2, await db.AgentSessions.CountAsync(s => s.WorkspaceId == world.A.WorkspaceId && s.Name == "worker-1", Ct));
    }

    [Fact]
    public async Task A_legacy_refresh_token_refreshes_into_a_token_for_the_sole_membership_and_with_two_is_refused()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var operatorId = await world.UserIdAsync(AppServer.Username);
        var browser = await server.SignInAsync(Ct);

        // Two clients signed in the way the server before this one signed them in: the operator by name, no tenant.
        world.Legacy.On = true;
        var legacy = await SignInAsync(world, browser, McpResource(world), Mcp);
        var second = await SignInAsync(world, browser, ApiResource(world), Api);
        world.Legacy.On = false;

        var old = await OAuthTests.ClaimsOfAsync(server, legacy.AccessToken);
        Assert.Equal(AppServer.Username, old.FindFirst("sub")?.Value);
        Assert.Null(old.FindFirst(RequestTenant.Claim));

        // Presented as it is, it acts in the user's only tenant, as it did.
        await AssertActsInAsync(world, legacy.AccessToken, TenantWorld.TitleA, TenantWorld.TitleB);

        // Refreshed, it comes back as a token of today's shape, with no sign-in: the user by id, that tenant.
        var upgraded = await OAuthTests.RefreshAsync(server.NewClient(), legacy.ClientId, legacy.RefreshToken!);
        await AssertTokenAsync(world, upgraded.AccessToken, operatorId, world.A.WorkspaceId);
        await AssertActsInAsync(world, upgraded.AccessToken, TenantWorld.TitleA, TenantWorld.TitleB);

        await world.MemberAsync(AppServer.Username, world.B.WorkspaceId);
        try
        {
            // With two memberships a legacy grant cannot be told which one it is for.
            var refused = await OAuthTests.TokenAsync(server.NewClient(), new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = second.RefreshToken!,
                ["client_id"] = second.ClientId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var error = await OAuthTests.ReadAsync(refused);
            Assert.Equal("invalid_grant", error.GetProperty("error").GetString());
            Assert.Contains("member of several", error.GetProperty("error_description").GetString(), StringComparison.Ordinal);
            Assert.Contains("Sign in again", error.GetProperty("error_description").GetString(), StringComparison.Ordinal);

            // The upgraded one names its tenant, so a second membership changes nothing for it.
            var again = await OAuthTests.RefreshAsync(server.NewClient(), upgraded.ClientId, upgraded.RefreshToken!);
            await AssertTokenAsync(world, again.AccessToken, operatorId, world.A.WorkspaceId);
        }
        finally
        {
            await world.LeaveAsync(AppServer.Username, world.B.WorkspaceId);
        }
    }

    [Fact]
    public async Task The_dcr_flow_foreman_and_agent_signin_use_still_succeeds_for_a_user()
    {
        var world = await TenantWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var user = await world.UserIdAsync(world.UserAB);
        var browser = await world.CookieAsync(world.UserAB);

        // Registered with the port-less loopback callback, authorized on whatever port the listener got, PKCE S256.
        var anonymous = server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous, "http://127.0.0.1/callback");
        const string redirect = "http://127.0.0.1:52042/callback";
        var pkce = OAuthTests.Pkce.Create();
        var code = await OAuthTests.AuthorizeForCodeAsync(browser, clientId, pkce, ApiResource(world), redirect, Api);
        var tokens = await OAuthTests.ExchangeAsync(anonymous, clientId, code, pkce, redirect);

        await AssertTokenAsync(world, tokens.AccessToken, user, world.A.WorkspaceId);
        Assert.Equal(HttpStatusCode.OK, (await world.Bearer(tokens.AccessToken).GetAsync("/api/v1/overview", Ct)).StatusCode);
        var minted = await TenantWorld.MintAsync(world.Bearer(tokens.AccessToken), "dcr-worker", "worker");
        await AssertActsInAsync(world, minted, TenantWorld.TitleA, TenantWorld.TitleB);

        var refreshed = await OAuthTests.RefreshAsync(server.NewClient(), clientId, tokens.RefreshToken!);
        await AssertTokenAsync(world, refreshed.AccessToken, user, world.A.WorkspaceId);
        Assert.Equal(HttpStatusCode.OK, (await world.Bearer(refreshed.AccessToken).GetAsync("/api/v1/overview", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_cimd_flow_claude_code_uses_still_succeeds_for_a_user()
    {
        var clientId = $"https://claude.example/{Guid.CreateVersion7():n}/client.json";
        var document = $$"""
            {"client_id":"{{clientId}}","client_name":"Claude Code","redirect_uris":["http://localhost/callback"]}
            """;

        var world = await TenantWorld.CreateAsync(fixture, builder => builder.Services.PostConfigure<HttpClientFactoryOptions>(
            ClientIdMetadata.HttpClientName,
            options => options.HttpMessageHandlerBuilderActions.Add(
                handler => handler.PrimaryHandler = new OAuthTests.MetadataDocument(clientId, document))));
        await using var server = world.Server;
        var user = await world.UserIdAsync(world.UserAB);
        var browser = await world.CookieAsync(world.UserAB);

        const string redirect = "http://localhost:43123/callback";
        var pkce = OAuthTests.Pkce.Create();
        var code = await OAuthTests.AuthorizeForCodeAsync(browser, clientId, pkce, McpResource(world), redirect);
        var tokens = await OAuthTests.ExchangeAsync(server.NewClient(), clientId, code, pkce, redirect);

        await AssertTokenAsync(world, tokens.AccessToken, user, world.A.WorkspaceId);
        await AssertActsInAsync(world, tokens.AccessToken, TenantWorld.TitleA, TenantWorld.TitleB);

        var refreshed = await OAuthTests.RefreshAsync(server.NewClient(), clientId, tokens.RefreshToken!);
        await AssertActsInAsync(world, refreshed.AccessToken, TenantWorld.TitleA, TenantWorld.TitleB);
    }

    private static string McpResource(TenantWorld world) => $"{world.Server.BaseAddress}/mcp";

    private static string ApiResource(TenantWorld world) => $"{world.Server.BaseAddress}/api";

    private static string Authorize(TenantWorld world, string clientId, OAuthTests.Pkce pkce)
        => OAuthTests.AuthorizeUrl(clientId, pkce, McpResource(world));

    /// <summary>A client registers, the browser authorizes it — approving when asked — and the client exchanges the code.</summary>
    private static async Task<OAuthTests.TokenSet> SignInAsync(TenantWorld world, HttpClient browser, string resource, string scope)
    {
        var anonymous = world.Server.NewClient();
        var clientId = await OAuthTests.RegisterAsync(anonymous);
        var pkce = OAuthTests.Pkce.Create();
        var code = await OAuthTests.AuthorizeForCodeAsync(browser, clientId, pkce, resource, scope: scope);
        return await OAuthTests.ExchangeAsync(anonymous, clientId, code, pkce);
    }

    private static async Task AssertTokenAsync(TenantWorld world, string accessToken, Guid user, Guid tenant)
    {
        var claims = await OAuthTests.ClaimsOfAsync(world.Server, accessToken);
        Assert.Equal(user.ToString(), claims.FindFirst("sub")?.Value);
        Assert.Equal(tenant.ToString(), Assert.Single(claims.FindAll(RequestTenant.Claim)).Value);
        Assert.Equal([WorkStatePolicies.Controller], claims.FindAll("role").Select(claim => claim.Value));
    }

    /// <summary>The task both tenants hold as <c>run-a/1.1</c> is <paramref name="own"/>, and nothing in <paramref name="never"/> is found.</summary>
    private static async Task AssertActsInAsync(TenantWorld world, string token, string own, params string[] never)
    {
        await using var mcp = await world.McpAsync(token);
        var read = await ReadServer.CallEitherAsync(mcp, "work_get", Args(("refs", new[] { "run-a/1.1" })), Ct);
        Assert.Contains(own, read.Text, StringComparison.Ordinal);

        var search = await ReadServer.CallEitherAsync(mcp, "work_search", Args(("q", TenantWorld.Marker)), Ct);
        foreach (var other in never)
        {
            Assert.DoesNotContain(other, read.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(other, search.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>Each use refused, each refresh refused, each with a reason that says to sign in again; the session minted with them gone.</summary>
    private static async Task AssertEndedAsync(
        TenantWorld world,
        OAuthTests.TokenSet mcp,
        OAuthTests.TokenSet api,
        string minted,
        string why)
    {
        foreach (var refused in new[]
                 {
                     await OAuthTests.McpAsync(world.Server, mcp.AccessToken),
                     await world.Bearer(api.AccessToken).GetAsync("/api/v1/overview", Ct),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var error = await ErrorAsync(refused);
            Assert.Contains(why, error, StringComparison.Ordinal);
            Assert.Contains("Sign in again", error, StringComparison.Ordinal);
        }

        foreach (var tokens in new[] { mcp, api })
        {
            var refreshed = await OAuthTests.TokenAsync(world.Server.NewClient(), new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken!,
                ["client_id"] = tokens.ClientId,
            });

            Assert.Equal(HttpStatusCode.BadRequest, refreshed.StatusCode);
            var body = await OAuthTests.ReadAsync(refreshed);
            Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
            Assert.Contains("Sign in again", body.GetProperty("error_description").GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await OAuthTests.McpAsync(world.Server, minted)).StatusCode);
    }

    /// <summary>What <c>Connect.tsx</c> submits: the page's query, the tenant it showed, the button pressed.</summary>
    private static Task<HttpResponseMessage> AnswerAsync(HttpClient browser, Uri consent, string answer, Guid shown)
    {
        var query = consent.OriginalString[consent.OriginalString.IndexOf('?', StringComparison.Ordinal)..];
        var fields = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query)
            .SelectMany(field => field.Value.Select(value => KeyValuePair.Create(field.Key, value ?? string.Empty)))
            .Append(KeyValuePair.Create(ConnectPages.TenantField, shown.ToString()))
            .Append(KeyValuePair.Create(ConnectPages.ConsentField, answer));

        return browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(fields), Ct);
    }

    /// <summary>The tenant of every permanent approval <paramref name="user"/> gave the client.</summary>
    private async Task<List<Guid>> ApprovalsAsync(Guid user, string clientId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return (await connection.QueryAsync<Guid>(
            $"""
             SELECT (a.properties::jsonb ->> '{OAuthEndpoints.TenantProperty}')::uuid
             FROM   openiddict_authorization a
             JOIN   openiddict_application p ON p.id = a.application_id
             WHERE  a.subject = @subject AND p.client_id = @clientId AND a.type = 'permanent' AND a.status = 'valid';
             """,
            new { subject = user.ToString(), clientId })).AsList();
    }

    private static async Task SwitchAsync(HttpClient cookie, Guid tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session/tenant") { Content = AppServer.Json(new { id = tenant }) };
        request.Headers.Add(ProjectEndpoints.WriteHeader, "1");
        Assert.Equal(HttpStatusCode.NoContent, (await cookie.SendAsync(request, Ct)).StatusCode);
    }

    private static Uri ConsentLocation(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("/connect/consent?", location.OriginalString, StringComparison.Ordinal);
        return location;
    }

    private static string CodeOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.True(location.IsAbsoluteUri, $"expected the client's callback, got {location}");

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.False(query.ContainsKey("error"), $"authorize failed: {location}");
        return query["code"].ToString();
    }

    private static string ErrorOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> MintAsync(TenantWorld world, string token, object body)
    {
        var response = await world.Bearer(token).PostAsync("/api/agent-sessions", AppServer.Json(body), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static Guid SessionId((HttpStatusCode Status, JsonElement Body) minted)
    {
        Assert.True(minted.Status == HttpStatusCode.Created, $"mint → {(int)minted.Status}: {minted.Body}");
        return minted.Body.GetProperty("sessionId").GetGuid();
    }

    private static string Token((HttpStatusCode Status, JsonElement Body) minted)
    {
        Assert.True(minted.Status == HttpStatusCode.Created, $"mint → {(int)minted.Status}: {minted.Body}");
        return minted.Body.GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> PatchAsync(TenantWorld world, string token, Guid session)
        => world.Bearer(token).PatchAsync($"/api/agent-sessions/{session}", AppServer.Json(new { claudeSessionId = $"c-{Guid.CreateVersion7():n}" }), Ct);

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;

    private static async Task<List<string>> ToolNamesAsync(ModelContextProtocol.Client.McpClient client)
        => (await client.ListToolsAsync(cancellationToken: Ct)).Select(tool => tool.Name).Order().ToList();

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);
}
