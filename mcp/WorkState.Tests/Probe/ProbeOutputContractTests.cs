using System.Text.Json;
using ModelContextProtocol.Protocol;
using WorkState.Abstractions.Probe;
using Xunit;

namespace WorkState.Tests.Probe;

/// <summary>
/// The output contract of <c>docs/workstate/01-mcp-surface.md</c> §2: every tool returns a compact
/// text rendering AND structured content, and they are not copies of each other. This is the one
/// thing in U0 that is easy to get wrong, so it is asserted over the real HTTP transport.
/// </summary>
public sealed class ProbeOutputContractTests(ProbeServerFixture server) : IClassFixture<ProbeServerFixture>
{
    [Fact]
    public async Task ToolsList_publishes_probe_echo_with_an_output_schema()
    {
        await using var client = await server.ConnectAsync(ProbeServerFixture.ReaderToken, TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var echo = tools.SingleOrDefault(t => t.Name == "probe_echo");

        Assert.NotNull(echo);
        Assert.NotNull(echo.ProtocolTool.OutputSchema);
        Assert.Contains("echoed", echo.ProtocolTool.OutputSchema.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeEcho_returns_structured_content_and_a_text_block_that_is_not_that_json()
    {
        await using var client = await server.ConnectAsync(ProbeServerFixture.ReaderToken, TestContext.Current.CancellationToken);

        var result = await client.CallToolAsync(
            "probe_echo",
            new Dictionary<string, object?> { ["message"] = "hello probe" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);

        var structuredJson = result.StructuredContent.Value.GetRawText();
        Assert.Contains(ProbeTokens.EchoStructured, structuredJson);
        Assert.Contains("hello probe", structuredJson);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.Contains(ProbeTokens.EchoText, text);
        Assert.DoesNotContain(ProbeTokens.EchoStructured, text);
        Assert.NotEqual(structuredJson, text);
        Assert.False(
            LooksLikeJson(text),
            $"content[0] must be the compact rendering, not serialized JSON. Got: {text}");
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
