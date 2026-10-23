using System.Net;
using System.Net.Http.Headers;
using WorkState.Api.App.V1;
using WorkState.Tests.Read;
using WorkState.Tests.Store;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.App.V1;

/// <summary>
/// doc 10 rule 5: the read API takes the dashboard cookie, any agent token, or a reader token — and a
/// reader token takes nothing else. Each refusal is paired with the same credential being admitted
/// somewhere, so a 401 here is about the route and never about a token that was simply wrong.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1AuthTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_reader_token_reads_the_overview_and_is_refused_on_mcp()
    {
        await using var server = await V1Http.StartAsync(fixture, $"reader-{Guid.CreateVersion7():n}");
        var reader = V1Http.Reader(server);

        await V1Http.GetAsync(reader, "/api/v1/overview");

        var initialize = AppServer.McpInitialize();
        initialize.Headers.Authorization = new AuthenticationHeaderValue("Bearer", V1World.ReaderToken);
        var refused = await reader.SendAsync(initialize, Ct);
        Assert.True(
            refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"initialize with a reader token → {(int)refused.StatusCode}");

        var list = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = AppServer.Json(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }),
        };
        list.Headers.Accept.ParseAdd("application/json, text/event-stream");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", V1World.ReaderToken);
        var listed = await reader.SendAsync(list, Ct);
        Assert.True(
            listed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"tools/list with a reader token → {(int)listed.StatusCode}");

        // The same transport admits an agent token, so the refusals are the reader's alone.
        await using var mcp = await server.ConnectAsync(ReadServer.WorkerToken, Ct);
        Assert.NotEmpty(await mcp.ListToolsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task A_reader_token_reaches_nothing_but_v1_and_the_stream()
    {
        await using var server = await V1Http.StartAsync(fixture, $"reader-{Guid.CreateVersion7():n}");
        var reader = V1Http.Reader(server);

        // Outside v1 and the stream, only /mcp and /api/session remain to refuse it.
        var initialize = AppServer.McpInitialize();
        initialize.Headers.Authorization = new AuthenticationHeaderValue("Bearer", V1World.ReaderToken);
        var mcp = await reader.SendAsync(initialize, Ct);
        Assert.True(
            mcp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"initialize with a reader token → {(int)mcp.StatusCode}");

        var signIn = await AppServer.SignInAsync(reader, AppServer.Username, AppServer.Password, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/session", Ct)).StatusCode);

        using var stream = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var opened = await reader.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, stream.Token);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("text/event-stream", opened.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Every_agent_token_and_the_cookie_read_v1()
    {
        await using var server = await V1Http.StartAsync(fixture, $"agents-{Guid.CreateVersion7():n}");

        foreach (var token in (string[])[ReadServer.WorkerToken, ReadServer.OrchestratorToken, ReadServer.ControllerToken])
        {
            await V1Http.GetAsync(V1Http.Bearer(server, token), "/api/v1/overview");
        }

        await V1Http.GetAsync(await server.SignInAsync(Ct), "/api/v1/overview");
    }

    [Fact]
    public async Task No_credential_is_a_401_that_still_carries_the_api_header()
    {
        await using var server = await V1Http.StartAsync(fixture, $"anon-{Guid.CreateVersion7():n}");
        var anonymous = server.NewClient();

        foreach (var route in (string[])["/api/v1/overview", "/api/v1/inbox", $"/api/v1/items/{Guid.CreateVersion7()}"])
        {
            var response = await anonymous.GetAsync(route, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(ApiV1.Version, Assert.Single(response.Headers.GetValues(ApiV1.Header)));
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, Ct)).StatusCode);

        var wrong = V1Http.Bearer(server, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/v1/overview", Ct)).StatusCode);
    }

    /// <summary>The compose default is an empty reader token, and empty must mean "none exists".</summary>
    [Fact]
    public async Task An_unset_reader_token_admits_no_one()
    {
        await using var server = await V1Http.StartAsync(fixture, $"unset-{Guid.CreateVersion7():n}", readerToken: "");
        var blank = server.NewClient();
        blank.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer ");

        Assert.Equal(HttpStatusCode.Unauthorized, (await blank.GetAsync("/api/v1/overview", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await V1Http.Reader(server).GetAsync("/api/v1/overview", Ct)).StatusCode);
    }
}
