using System.Net;
using System.Net.Http.Headers;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App;

/// <summary>
/// What the host serves besides the read API: the SPA shell, and the boundary between it and the
/// two APIs. The read endpoints themselves are asserted in <c>V1/V1ReadTests</c> (doc 10).
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AppReadTests(PostgresFixture fixture)
{
    /// <summary>
    /// The SPA shell answers what nothing else claimed, and nothing more. Registered before
    /// <c>/mcp</c> or <c>/api</c> it would answer an agent's tool call with a page of HTML and a
    /// 200, which is the failure that looks like success.
    /// </summary>
    [Fact]
    public async Task The_static_fallback_shadows_neither_api_nor_mcp()
    {
        await using var server = await StartAsync($"fallback-{Guid.CreateVersion7():n}");
        var client = server.NewClient();

        var shell = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal("text/html", shell.Content.Headers.ContentType?.MediaType);

        // No committed placeholder file: wwwroot is the frontend's build output and is gitignored,
        // so the host owns the sentence a clone without Node sees.
        var body = await shell.Content.ReadAsStringAsync(Ct);
        Assert.Contains("not built yet", body, StringComparison.Ordinal);
        Assert.Contains("/api", body, StringComparison.Ordinal);

        // A client-routed path the SPA owns still gets the shell.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/runs/run-a", Ct)).StatusCode);

        // But the two APIs keep their own answers.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/no-such-route", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mcp/no-such-route", Ct)).StatusCode);

        var mcp = await client.SendAsync(AppServer.McpInitialize(), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);

        var bearer = AppServer.McpInitialize();
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReadServer.WorkerToken);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(bearer, Ct)).StatusCode);
    }

    private Task<AppServer> StartAsync(string slug)
        => AppServer.StartAsync(fixture.ConnectionString, slug, Ct);
}
