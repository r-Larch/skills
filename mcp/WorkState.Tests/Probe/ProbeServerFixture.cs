using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using WorkState.Api;
using Xunit;

namespace WorkState.Tests.Probe;

/// <summary>
/// Starts the real host on a loopback port so the tests speak Streamable HTTP to it exactly as
/// Claude Code would. No test-host shim: if the transport, the auth pipeline or MapMcp is wrong,
/// these tests see it.
/// </summary>
public sealed class ProbeServerFixture : IAsyncLifetime
{
    public const string ReaderToken = "test-reader-token-2f8a4c";
    public const string WriterToken = "test-writer-token-9c1d7e";

    private WebApplication? _app;

    public string BaseAddress { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _app = ApiHost.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkState:Auth:ReaderToken"] = ReaderToken,
                ["WorkState:Auth:WriterToken"] = WriterToken,
            });
        });

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        BaseAddress = addresses!.Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public Task<McpClient> ConnectAsync(string token, CancellationToken cancellationToken = default)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{BaseAddress}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token}",
            },
        });

        return McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }
}
