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
    [
        "/api/v1/overview", "/api/v1/inbox", "/api/v1/journal",
        $"/api/v1/items/{Guid.Empty}", $"/api/v1/items/{Guid.Empty}/brief", $"/api/v1/runs/{Guid.Empty}/board",
    ];

    [Fact]
    public async Task A_dashboard_cookie_cannot_reach_mcp()
    {
        await using var server = await StartAsync();
        var client = await server.SignInAsync(Ct);

        // The cookie is real: it opens the App API on this very client.
        var overview = await client.GetAsync("/api/v1/overview", Ct);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);

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
        // A database with no user yet: in the shared one, whichever test signed in first created the operator.
        await using var database = await FreshDatabase.CreateAsync(fixture, Ct);
        await using var server = await AppServer.StartAsync(
            database.ConnectionString, $"auth-{Guid.CreateVersion7():n}", Ct, dashboardConfigured: false);
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);

        var right = await AppServer.SignInAsync(client, AppServer.Username, AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/session", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }

    private Task<AppServer> StartAsync()
        => AppServer.StartAsync(
            fixture.ConnectionString,
            $"auth-{Guid.CreateVersion7():n}",
            Ct);
}
