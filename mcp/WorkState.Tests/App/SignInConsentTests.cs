using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.App.OAuthTests;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// The human's half of <c>/connect/authorize</c>, in the style of <see cref="OAuthTests"/>: a browser with no
/// session is sent to sign in and brought back, a first authorize for a client asks before it issues a code,
/// an approval is remembered and a refusal is not, the sign-in page forwards to nowhere but this server's
/// authorize endpoint, and both anonymous writes are throttled.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class SignInConsentTests(PostgresFixture fixture)
{
    private const string Callback = "http://127.0.0.1:7777/callback";

    [Fact]
    public async Task Signed_out_the_browser_signs_in_approves_and_reaches_the_client_with_a_code()
    {
        await using var server = await StartAsync();
        var browser = server.NewClient();
        var clientId = await RegisterAsync(browser);
        var pkce = Pkce.Create();

        var authorize = await browser.GetAsync(AuthorizeUrl(clientId, pkce, Resource(server)), Ct);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var returnUrl = SignInReturnUrl(authorize);

        var signInPage = await browser.GetAsync(authorize.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.OK, signInPage.StatusCode);
        Assert.Equal("text/html", signInPage.Content.Headers.ContentType?.MediaType);
        Assert.Contains("frame-ancestors 'none'", Csp(signInPage), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await AppServer.SignInAsync(browser, AppServer.Username, AppServer.Password, Ct)).StatusCode);

        var back = await browser.GetAsync(returnUrl, Ct);
        var consent = ConsentLocation(back);

        var consentPage = await browser.GetAsync(consent, Ct);
        Assert.Equal(HttpStatusCode.OK, consentPage.StatusCode);
        Assert.Contains("form-action 'self' http://127.0.0.1:7777;", Csp(consentPage), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", Csp(consentPage), StringComparison.Ordinal);

        var info = await AppInfoAsync(browser, clientId, Callback);
        Assert.Equal("OAuth tests", info.GetProperty("clientName").GetString());
        Assert.Equal("127.0.0.1", info.GetProperty("redirectHost").GetString());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("clientHost").ValueKind);
        Assert.True(info.GetProperty("loopbackOnly").GetBoolean());

        var approved = await AnswerConsentAsync(browser, consent, "granted");
        var code = CodeFrom(approved);

        var tokens = await ExchangeAsync(server.NewClient(), clientId, code, pkce);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(server, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_first_authorize_without_consent_yields_no_code()
    {
        await using var server = await StartAsync();
        var clientId = await RegisterAsync(server.NewClient());
        var browser = await server.SignInAsync(Ct);

        var response = await browser.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), Resource(server)), Ct);
        var consent = ConsentLocation(response);

        Assert.DoesNotContain(Callback, consent.OriginalString, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(consent.OriginalString[consent.OriginalString.IndexOf('?', StringComparison.Ordinal)..]);
        Assert.False(query.ContainsKey("code"));
        Assert.Equal(clientId, query["client_id"].ToString());
    }

    [Fact]
    public async Task An_approval_is_remembered_so_the_second_authorize_skips_consent()
    {
        await using var server = await StartAsync();
        var anonymous = server.NewClient();
        var clientId = await RegisterAsync(anonymous);
        var browser = await server.SignInAsync(Ct);

        var first = await browser.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), Resource(server)), Ct);
        CodeFrom(await AnswerConsentAsync(browser, ConsentLocation(first), "granted"));

        // Straight to the client: no consent page, no POST.
        var pkce = Pkce.Create();
        var second = await browser.GetAsync(AuthorizeUrl(clientId, pkce, Resource(server)), Ct);
        var tokens = await ExchangeAsync(anonymous, clientId, CodeFrom(second), pkce);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(server, tokens.AccessToken)).StatusCode);

        // The approval belongs to that client alone.
        var other = await RegisterAsync(anonymous);
        ConsentLocation(await browser.GetAsync(AuthorizeUrl(other, Pkce.Create(), Resource(server)), Ct));

        // A Deny from a consent page still open in another tab means deny, approval or not.
        var stale = await AnswerConsentAsync(browser, ConsentLocation(first), "denied");
        Assert.Equal("access_denied", QueryHelpers.ParseQuery(stale.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task A_refusal_returns_access_denied_to_the_client_and_is_not_remembered()
    {
        await using var server = await StartAsync();
        var clientId = await RegisterAsync(server.NewClient());
        var browser = await server.SignInAsync(Ct);

        var first = await browser.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), Resource(server)), Ct);
        var denied = await AnswerConsentAsync(browser, ConsentLocation(first), "denied");

        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        var location = denied.Headers.Location!;
        Assert.Equal(Callback, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal("state-1", query["state"].ToString());
        Assert.False(query.ContainsKey("code"));

        ConsentLocation(await browser.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), Resource(server)), Ct));
    }

    [Theory]
    [InlineData("https://evil.example/connect/authorize?client_id=x")]
    [InlineData("//evil.example/connect/authorize?client_id=x")]
    [InlineData("/\\evil.example/connect/authorize")]
    [InlineData("/connect/authorizeX?client_id=x")]
    [InlineData("/connect/authorize/../../api/v1/overview")]
    [InlineData("/api/v1/overview")]
    [InlineData("")]
    [InlineData(null)]
    public async Task The_sign_in_page_refuses_a_return_address_that_is_not_this_servers_authorize(string? returnUrl)
    {
        await using var server = await StartAsync();
        var anonymous = server.NewClient();

        var refused = await anonymous.GetAsync(QueryHelpers.AddQueryString("/connect/signin", "returnUrl", returnUrl ?? string.Empty), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(refused.Headers.Location);

        if (returnUrl is null)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync("/connect/signin", Ct)).StatusCode);
        }

        var accepted = await anonymous.GetAsync(
            QueryHelpers.AddQueryString("/connect/signin", "returnUrl", "/connect/authorize?client_id=x"), Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task The_consent_page_without_a_session_goes_back_through_sign_in()
    {
        await using var server = await StartAsync();
        var anonymous = server.NewClient();
        var clientId = await RegisterAsync(anonymous);

        var consent = QueryHelpers.AddQueryString("/connect/consent", new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = Callback,
        });
        var response = await anonymous.GetAsync(consent, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/connect/authorize" + consent["/connect/consent".Length..], SignInReturnUrl(response));
    }

    [Fact]
    public async Task App_info_needs_the_session_and_names_only_a_registered_callback()
    {
        await using var server = await StartAsync();
        var anonymous = server.NewClient();
        var clientId = await RegisterAsync(anonymous);

        var unsigned = await anonymous.GetAsync($"/connect/app-info?client_id={clientId}", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Null(unsigned.Headers.Location);

        var browser = await server.SignInAsync(Ct);
        var elsewhere = await AppInfoAsync(browser, clientId, "https://attacker.example/callback");
        Assert.Equal("OAuth tests", elsewhere.GetProperty("clientName").GetString());
        Assert.Equal(JsonValueKind.Null, elsewhere.GetProperty("redirectHost").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/connect/app-info?client_id=nobody", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync("/connect/app-info", Ct)).StatusCode);
    }

    [Fact]
    public async Task Prompt_none_answers_the_client_instead_of_showing_a_page()
    {
        await using var server = await StartAsync();
        var anonymous = server.NewClient();
        var clientId = await RegisterAsync(anonymous);

        var signedOut = await anonymous.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), Resource(server)) + "&prompt=none", Ct);
        Assert.Equal("login_required", ErrorFrom(signedOut));

        var browser = await server.SignInAsync(Ct);
        var unapproved = await browser.GetAsync(AuthorizeUrl(clientId, Pkce.Create(), Resource(server)) + "&prompt=none", Ct);
        Assert.Equal("consent_required", ErrorFrom(unapproved));
    }

    [Fact]
    public async Task Repeated_sign_in_failures_are_refused_with_429_and_never_name_the_wrong_field()
    {
        await using var server = await StartAsync();
        var client = server.NewClient();

        var wrongPassword = await AppServer.SignInAsync(client, AppServer.Username, "not-the-password", Ct);
        var wrongUsername = await AppServer.SignInAsync(client, "not-the-operator", AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongUsername.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(Ct), await wrongUsername.Content.ReadAsStringAsync(Ct));
        Assert.Equal(string.Empty, await wrongPassword.Content.ReadAsStringAsync(Ct));

        for (var attempt = 2; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await AppServer.SignInAsync(client, AppServer.Username, $"guess-{attempt}", Ct)).StatusCode);
        }

        // Past the limit even the right password is not looked at.
        var throttled = await AppServer.SignInAsync(client, AppServer.Username, AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.RetryAfter is not null, "429 without Retry-After");
    }

    [Fact]
    public async Task Registration_is_rate_limited()
    {
        await using var server = await StartAsync();
        var client = server.NewClient();

        for (var i = 0; i < 10; i++)
        {
            await RegisterAsync(client);
        }

        var refused = await client.PostAsync(
            "/connect/register",
            AppServer.Json(new { client_name = "one too many", redirect_uris = new[] { Callback } }),
            Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    /// <summary>
    /// The session cookie stays SameSite=Strict (the SameSite decision in the m39 report): a CLI's browser
    /// arrives top-level and carries it, and a cross-site arrival lands on the sign-in page, which asks
    /// <c>GET /api/session</c> — same-site, so the cookie goes — and continues at once when it is there.
    /// </summary>
    [Fact]
    public async Task The_session_cookie_stays_strict_and_the_sign_in_page_can_ask_whether_it_is_there()
    {
        await using var server = await StartAsync();
        var client = server.NewClient();

        var probe = await client.GetAsync("/api/session", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
        Assert.Null(probe.Headers.Location);

        var signIn = await AppServer.SignInAsync(client, AppServer.Username, AppServer.Password, Ct);
        var cookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie"), value => value.StartsWith("workstate.dashboard=", StringComparison.Ordinal));
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/session", Ct)).StatusCode);
    }

    private Task<AppServer> StartAsync()
        => AppServer.StartAsync(fixture.ConnectionString, $"signin-{Guid.CreateVersion7():n}", Ct);

    private static string Resource(AppServer server) => $"{server.BaseAddress}/mcp";

    private static Uri ConsentLocation(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("/connect/consent?", location.OriginalString, StringComparison.Ordinal);
        return location;
    }

    private static string CodeFrom(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.True(location.IsAbsoluteUri, $"expected the client's callback, got {location}");
        Assert.Equal(Callback, location.GetLeftPart(UriPartial.Path));

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.False(query.ContainsKey("error"), $"authorize failed: {location}");
        return query["code"].ToString();
    }

    private static string ErrorFrom(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(Callback, location.GetLeftPart(UriPartial.Path));
        return QueryHelpers.ParseQuery(location.Query)["error"].ToString();
    }

    private static string Csp(HttpResponseMessage response)
        => string.Join(", ", response.Headers.TryGetValues("Content-Security-Policy", out var values) ? values : []);

    private static async Task<JsonElement> AppInfoAsync(HttpClient browser, string clientId, string redirectUri)
        => await AppServer.ReadJsonAsync(
            browser,
            QueryHelpers.AddQueryString("/connect/app-info", new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
            }),
            Ct);
}
