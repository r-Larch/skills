using System.Net;
using System.Net.Http.Headers;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// doc 08 §4's non-negotiable: one process, two audiences, and two authentication schemes that
/// never satisfy each other. Both directions are asserted, because a one-way test would pass on a
/// host that had merged the two into a single permissive scheme.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AppAuthTests(PostgresFixture fixture)
{
    private static readonly string[] AppRoutes =
        ["/api/projects", "/api/watch", "/api/runs/run-a/tree", "/api/runs/run-a/brief", "/api/runs/run-a/journal"];

    [Fact]
    public async Task A_bearer_token_cannot_reach_the_app_api_however_privileged_it_is()
    {
        await using var server = await StartAsync();
        var client = server.NewClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ReadServer.ControllerToken);

        foreach (var route in AppRoutes)
        {
            var response = await client.GetAsync(route, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // The same token on the transport it belongs to, so the 401s above are about the scheme and
        // not about a token that was simply wrong.
        await using var mcp = await server.ConnectAsync(ReadServer.ControllerToken, Ct);
        Assert.NotEmpty(await mcp.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task A_dashboard_cookie_cannot_reach_mcp()
    {
        await using var server = await StartAsync();
        var client = await server.SignInAsync(Ct);

        // The cookie is real: it opens the App API on this very client.
        var watch = await client.GetAsync("/api/watch", Ct);
        Assert.Equal(HttpStatusCode.OK, watch.StatusCode);

        var refused = await client.SendAsync(AppServer.McpInitialize(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // And the same request with the bearer the agents use is admitted, so the 401 above is the
        // scheme boundary rather than a malformed body.
        var bearer = AppServer.McpInitialize();
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadServer.WorkerToken);

        var admitted = await client.SendAsync(bearer, Ct);
        Assert.NotEqual(HttpStatusCode.Unauthorized, admitted.StatusCode);
    }

    [Fact]
    public async Task With_no_dashboard_credential_configured_login_is_refused_and_every_api_route_is_401()
    {
        await using var server = await StartAsync(dashboardConfigured: false);
        var client = server.NewClient();

        var withCredentials = await AppServer.SignInAsync(
            client, AppServer.Username, AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, withCredentials.StatusCode);

        // The blank configuration must not be satisfiable by presenting blanks either.
        var withBlanks = await AppServer.SignInAsync(client, string.Empty, string.Empty, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, withBlanks.StatusCode);

        var withNulls = await AppServer.SignInAsync(client, null, null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, withNulls.StatusCode);

        foreach (var route in AppRoutes)
        {
            var response = await client.GetAsync(route, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var stream = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, stream.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_signing_out_closes_the_session()
    {
        await using var server = await StartAsync();
        var client = server.NewClient();

        var wrong = await AppServer.SignInAsync(client, AppServer.Username, "not-the-password", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/watch", Ct)).StatusCode);

        var right = await AppServer.SignInAsync(client, AppServer.Username, AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/watch", Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/session", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/watch", Ct)).StatusCode);
    }

    private Task<AppServer> StartAsync(bool dashboardConfigured = true)
        => AppServer.StartAsync(
            fixture.ConnectionString,
            $"auth-{Guid.CreateVersion7():n}",
            Ct,
            dashboardConfigured);
}
