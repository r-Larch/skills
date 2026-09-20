using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkState.Api;

namespace WorkState.Tests.Read;

/// <summary>
/// The real host on a loopback port, pointed at a Testcontainers database, spoken to over
/// Streamable HTTP by the SDK client — the same path Claude Code takes. No test-host shim: if the
/// transport, the auth pipeline, <c>MapMcp</c> or the DI wiring is wrong, these tests see it.
/// </summary>
internal sealed class ReadServer : IAsyncDisposable
{
    public const string WorkerToken = "test-worker-token-4b1f7a";
    public const string OrchestratorToken = "test-orchestrator-token-8d2c6e";
    public const string ControllerToken = "test-controller-token-c93a15";

    private readonly WebApplication _app;

    private ReadServer(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    public static async Task<ReadServer> StartAsync(
        string connectionString,
        string? workspaceSlug,
        CancellationToken cancellationToken)
    {
        var app = ApiHost.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WorkState"] = connectionString,
                ["WorkState:Workspace"] = workspaceSlug,
                ["WorkState:Auth:WorkerToken"] = WorkerToken,
                ["WorkState:Auth:OrchestratorToken"] = OrchestratorToken,
                ["WorkState:Auth:ControllerToken"] = ControllerToken,
            });
        });

        await app.StartAsync(cancellationToken);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new ReadServer(app, addresses!.Addresses.First());
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

    /// <summary>
    /// Calls a tool and asserts the output contract of doc 01 §2 on the way past: exactly one text
    /// block and no structured content. Every read in these tests goes through here, so the
    /// contract the probe cost two rounds to establish cannot regress unnoticed.
    /// </summary>
    public static async Task<string> CallAsync(
        McpClient client,
        string tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(
            tool,
            arguments?.ToDictionary(a => a.Key, a => a.Value),
            cancellationToken: cancellationToken);

        var rendered = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        Xunit.Assert.False(result.IsError is true, $"{tool} returned isError: {rendered}");
        Xunit.Assert.Null(result.StructuredContent);

        var block = Xunit.Assert.IsType<TextContentBlock>(Xunit.Assert.Single(result.Content));
        return block.Text;
    }

    /// <summary>
    /// The same contract, but a refusal is an expected outcome rather than a failure: a write tool
    /// answers <c>isError: true</c> with the remedial text of doc 06 §6, and that text is the thing
    /// under test.
    /// </summary>
    public static async Task<(string Text, bool Refused)> CallEitherAsync(
        McpClient client,
        string tool,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(
            tool,
            arguments?.ToDictionary(a => a.Key, a => a.Value),
            cancellationToken: cancellationToken);

        Xunit.Assert.Null(result.StructuredContent);
        var block = Xunit.Assert.IsType<TextContentBlock>(Xunit.Assert.Single(result.Content));

        return (block.Text, result.IsError is true);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
