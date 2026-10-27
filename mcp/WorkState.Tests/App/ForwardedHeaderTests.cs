using System.Net;
using WorkState.Api.Auth;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using WorkState.Tests.Write;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// Unit <c>shared-v1/origin</c>: behind a reverse proxy the web line, the <c>/r</c> redirect and the
/// dashboard cookie follow the origin the proxy was reached on — but only when the proxy's address is
/// in <c>WORKSTATE_TRUSTED_PROXIES</c>. The server listens on a real loopback socket, so the proxy the
/// tests stand in for is <c>127.0.0.1</c>, trusted (or not) explicitly by address.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ForwardedHeaderTests(PostgresFixture fixture)
{
    private const string Loopback = "127.0.0.1";

    private const string Elsewhere = "10.20.30.40, 192.0.2.0/24";

    private static readonly Dictionary<string, string> Forwarded = new()
    {
        ["X-Forwarded-Proto"] = "https",
        ["X-Forwarded-Host"] = "x.example",
        ["X-Forwarded-For"] = "203.0.113.7",
    };

    [Fact]
    public async Task With_no_public_url_and_no_trusted_proxy_the_request_host_is_used_and_forwarded_headers_are_not_read()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);

        Assert.Equal($"{server.BaseAddress}/r/{project}/run-a/1.1", await WebLineAsync(server, Forwarded));
        Assert.False(IsSecure(await SignInCookieAsync(server, Forwarded)));
    }

    [Fact]
    public async Task A_trusted_proxy_sets_the_scheme_and_host_of_the_web_line_the_r_redirect_and_the_cookie()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);

        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString, world.Slug, Ct, trustedProxies: Loopback);

        var link = await WebLineAsync(server, Forwarded);
        Assert.Equal($"https://x.example/r/{project}/run-a/1.1", link);

        var cookie = await SignInCookieAsync(server, Forwarded);
        Assert.True(IsSecure(cookie), cookie);

        // A Secure cookie never travels back over the test's plain socket on its own, so it is sent
        // the way the browser on the proxy's https side would send it.
        using var client = server.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(link).PathAndQuery);
        AddHeaders(request, Forwarded);
        request.Headers.Add("Cookie", cookie.Split(';')[0]);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.False(location.IsAbsoluteUri, location.OriginalString);
        Assert.Equal($"https://x.example/runs/{world.RunId}/{world.TaskId}", new Uri(new Uri(link), location).ToString());
    }

    [Fact]
    public async Task Forwarded_headers_from_an_address_that_is_not_trusted_are_ignored()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);

        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString, world.Slug, Ct, trustedProxies: Elsewhere);

        Assert.Equal($"{server.BaseAddress}/r/{project}/run-a/1.1", await WebLineAsync(server, Forwarded));
        Assert.False(IsSecure(await SignInCookieAsync(server, Forwarded)));
    }

    [Fact]
    public async Task An_https_public_url_names_the_origin_and_makes_the_cookie_secure_even_without_a_trusted_proxy()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world);

        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString, world.Slug, Ct, publicUrl: "https://x.example");

        Assert.Equal($"https://x.example/r/{project}/run-a/1.1", await WebLineAsync(server, Forwarded));
        Assert.True(IsSecure(await SignInCookieAsync(server, new Dictionary<string, string>())));
    }

    [Fact]
    public async Task A_malformed_trusted_proxy_entry_stops_the_host_from_starting()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct, trustedProxies: "127.0.0.1, proxy.lan"));

        Assert.Contains("proxy.lan", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<string> WebLineAsync(AppServer server, IReadOnlyDictionary<string, string> headers)
    {
        await using var mcp = await server.ConnectAsync(ReadServer.ControllerToken, Ct, headers);
        var brief = await ReadServer.CallAsync(mcp, "work_brief", new Dictionary<string, object?> { ["scope"] = "1.1" }, Ct);
        var line = Assert.Single(brief.Split('\n'), l => l.StartsWith("web      ", StringComparison.Ordinal));
        return line[9..];
    }

    private static async Task<string> SignInCookieAsync(AppServer server, IReadOnlyDictionary<string, string> headers)
    {
        using var client = server.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
        {
            Content = AppServer.Json(new { username = AppServer.Username, password = AppServer.Password }),
        };
        AddHeaders(request, headers);

        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        return Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(DashboardAuth.CookieName + "=", StringComparison.Ordinal));
    }

    private static bool IsSecure(string setCookie)
        => setCookie.Split(';').Any(a => a.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase));

    private static void AddHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }
    }

    private static async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var db = world.Fixture.NewContext();
        return (await db.Items.FindAsync([world.ProjectId], Ct))!.Ref;
    }
}
