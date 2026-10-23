using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using WorkState.Api;
using WorkState.Tests.Read;
using Xunit;

namespace WorkState.Tests.App;

/// <summary>
/// The real host on a loopback port with both audiences wired: the bearer scheme the agents use and
/// the cookie scheme the dashboard uses. Same posture as <see cref="ReadServer"/> and for the same
/// reason — a test host shim would let a pipeline bug through, and the pipeline is what doc 08 §4's
/// "two schemes that never cross" is made of.
/// </summary>
internal sealed class AppServer : IAsyncDisposable
{
    public const string Username = "operator";
    public const string Password = "test-dashboard-password-3f9c21";

    private readonly WebApplication _app;
    private readonly List<HttpClient> _clients = [];

    private AppServer(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<AppServer> StartAsync(
        string connectionString,
        string? workspaceSlug,
        CancellationToken cancellationToken,
        bool dashboardConfigured = true,
        string? readerToken = null)
    {
        var app = ApiHost.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WorkState"] = connectionString,
                ["WorkState:Workspace"] = workspaceSlug,
                ["WorkState:Auth:WorkerToken"] = ReadServer.WorkerToken,
                ["WorkState:Auth:OrchestratorToken"] = ReadServer.OrchestratorToken,
                ["WorkState:Auth:ControllerToken"] = ReadServer.ControllerToken,
                ["WorkState:Auth:ReaderToken"] = readerToken,
                ["WorkState:Dashboard:Username"] = dashboardConfigured ? Username : null,
                ["WorkState:Dashboard:Password"] = dashboardConfigured ? Password : null,
            });
        });

        await app.StartAsync(cancellationToken);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new AppServer(app, addresses!.Addresses.First());
    }

    /// <summary>A client with a jar of its own, so one test's session cannot leak into another's.</summary>
    public HttpClient NewClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false,
        })
        {
            BaseAddress = new Uri(BaseAddress),
        };

        _clients.Add(client);
        return client;
    }

    public async Task<HttpClient> SignInAsync(CancellationToken cancellationToken)
    {
        var client = NewClient();
        var response = await SignInAsync(client, Username, Password, cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    public static Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string? username,
        string? password,
        CancellationToken cancellationToken)
        => client.PostAsync(
            "/api/session",
            Json(new { username, password }),
            cancellationToken);

    public static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>
    /// The smallest well-formed thing an MCP client would send. The body is beside the point: the
    /// authorization filter runs before the handler, so what these tests read off it is whether the
    /// credential was admitted at all.
    /// </summary>
    public static HttpRequestMessage McpInitialize()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = Json(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "workstate-tests", version = "0.0.0" },
                },
            }),
        };

        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    public Task<McpClient> ConnectAsync(string token, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{BaseAddress}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        });

        return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    public static async Task<JsonElement> ReadJsonAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
