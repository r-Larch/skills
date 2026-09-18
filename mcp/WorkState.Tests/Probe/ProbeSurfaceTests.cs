using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WorkState.Abstractions.Probe;
using WorkState.Mcp.Probe;
using Xunit;

namespace WorkState.Tests.Probe;

/// <summary>
/// The five primitives plus the writer-scoped tool are reachable over the wire. These prove the
/// server publishes what <c>mcp/PROBE.md</c> asks the controller to look for — they cannot say
/// what a client renders, which is the whole point of the manual probe.
/// </summary>
public sealed class ProbeSurfaceTests(ProbeServerFixture server) : IClassFixture<ProbeServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ServerInstructions_reach_the_client_with_the_greppable_token()
    {
        await using var client = await server.ConnectAsync(ProbeServerFixture.ReaderToken, Ct);

        Assert.NotNull(client.ServerInstructions);
        Assert.Contains(ProbeTokens.ServerInstructions, client.ServerInstructions);
    }

    [Fact]
    public async Task ProbeLink_returns_a_resource_link_to_the_fixed_resource()
    {
        await using var client = await server.ConnectAsync(ProbeServerFixture.ReaderToken, Ct);

        var result = await client.CallToolAsync("probe_link", cancellationToken: Ct);

        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        Assert.Equal(ProbeUris.Fixed, link.Uri);
        Assert.Contains(ProbeTokens.ResourceLink, link.Title);
    }

    [Fact]
    public async Task Prompt_is_listed_with_one_required_argument_and_renders()
    {
        await using var client = await server.ConnectAsync(ProbeServerFixture.ReaderToken, Ct);

        var prompts = await client.ListPromptsAsync(cancellationToken: Ct);
        var probe = Assert.Single(prompts, p => p.Name == "probe_prompt");

        var argument = Assert.Single(probe.ProtocolPrompt.Arguments!);
        Assert.Equal("topic", argument.Name);
        Assert.True(argument.Required);

        var rendered = await client.GetPromptAsync(
            "probe_prompt",
            new Dictionary<string, object?> { ["topic"] = "surfacing" },
            cancellationToken: Ct);

        var text = string.Join(
            "\n",
            rendered.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(c => c.Text));

        Assert.Contains(ProbeTokens.Prompt, text);
        Assert.Contains("surfacing", text);
    }

    [Fact]
    public async Task Fixed_resource_is_listed_and_readable()
    {
        await using var client = await server.ConnectAsync(ProbeServerFixture.ReaderToken, Ct);

        var resources = await client.ListResourcesAsync(cancellationToken: Ct);
        Assert.Contains(resources, r => r.Uri == ProbeUris.Fixed);

        var read = await client.ReadResourceAsync(ProbeUris.Fixed, cancellationToken: Ct);
        var body = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;

        Assert.Contains(ProbeTokens.FixedResource, body);
    }

    [Fact]
    public async Task Resource_template_is_listed_and_completes_the_id_variable()
    {
        await using var client = await server.ConnectAsync(ProbeServerFixture.ReaderToken, Ct);

        var templates = await client.ListResourceTemplatesAsync(cancellationToken: Ct);
        Assert.Contains(templates, t => t.UriTemplate == ProbeUris.Template);

        var completion = await client.CompleteAsync(
            new ResourceTemplateReference { Uri = ProbeUris.Template },
            argumentName: "id",
            argumentValue: string.Empty,
            cancellationToken: Ct);

        Assert.Contains(ProbeTokens.Completion, completion.Completion.Values);
    }

    [Fact]
    public async Task Writer_only_tool_is_refused_for_a_reader_token_and_allowed_for_a_writer_token()
    {
        await using var reader = await server.ConnectAsync(ProbeServerFixture.ReaderToken, Ct);

        var refused = await Record.ExceptionAsync(async () =>
        {
            var result = await reader.CallToolAsync("probe_writer_only", cancellationToken: Ct);
            Assert.True(result.IsError, "a reader token must not be able to run probe_writer_only");
        });
        Assert.True(
            refused is null or McpException,
            $"expected a refusal, got {refused?.GetType().Name}: {refused?.Message}");

        await using var writer = await server.ConnectAsync(ProbeServerFixture.WriterToken, Ct);
        var allowed = await writer.CallToolAsync("probe_writer_only", cancellationToken: Ct);

        Assert.NotEqual(true, allowed.IsError);
        Assert.Contains(
            ProbeTokens.WriterOnly,
            Assert.IsType<TextContentBlock>(Assert.Single(allowed.Content)).Text);
    }

    [Fact]
    public async Task An_unknown_token_cannot_reach_the_mcp_endpoint_at_all()
    {
        var failure = await Record.ExceptionAsync(() => server.ConnectAsync("not-a-real-token", Ct));

        Assert.NotNull(failure);
    }
}
