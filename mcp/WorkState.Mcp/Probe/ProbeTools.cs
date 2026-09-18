using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WorkState.Abstractions.Probe;

namespace WorkState.Mcp.Probe;

/// <summary>
/// The probe tool surface. Nothing here is a real WorkState tool — each one exists to make a
/// single client behaviour observable. See <c>mcp/PROBE.md</c> for the questions and the table
/// the controller fills in.
/// </summary>
[McpServerToolType]
public static class ProbeTools
{
    [McpServerTool(
        Name = "probe_echo",
        Title = "Probe: echo (text + structured, deliberately not duplicated)",
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ProbeResult))]
    [Description("""
        PROBE Q1 — does a hand-built CallToolResult still publish an outputSchema, and does the
        client show the text block, the JSON, or both?
        Returns a one-line compact text rendering AND a structuredContent object that are NOT
        copies of each other (docs/workstate/01-mcp-surface.md §2). The text carries the marker
        WSPROBE-TEXT-A1B2C3; the JSON carries WSPROBE-JSON-D4E5F6. Whichever marker you can see
        is the channel that reached you.
        Next: call probe_link to see whether resource links render.
        """)]
    public static CallToolResult Echo(
        [Description("Any string. It comes back in both halves of the result.")] string message)
    {
        var result = new ProbeResult
        {
            Echoed = message,
            Length = message.Length,
            ServerTimeUtc = DateTimeOffset.UtcNow.ToString("O"),
            Answers = "Q1 outputSchema + structuredContent on a hand-built CallToolResult",
            ProbeToken = ProbeTokens.EchoStructured,
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"probe_echo ok  len={result.Length}  {ProbeTokens.EchoText}  "
                         + "(Q1: this is the text block; the JSON is in structuredContent, not here)",
                },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(result, ProbeJson.Options),
        };
    }

    [McpServerTool(
        Name = "probe_link",
        Title = "Probe: resource link in a tool result",
        ReadOnly = true)]
    [Description("""
        PROBE Q2 — does the client render, or follow, a resource_link returned by a tool?
        Returns one text line plus a resource_link pointing at work://probe/fixed, the same fixed
        resource probe Q4 publishes. If the link is rendered as something followable, doc 01 §2's
        "resource links instead of payloads" strategy holds.
        Next: read work://probe/fixed and compare.
        """)]
    public static CallToolResult Link()
        => new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"probe_link ok  {ProbeTokens.ResourceLink}  "
                         + $"(Q2: one resource_link follows, pointing at {ProbeUris.Fixed})",
                },
                new ResourceLinkBlock
                {
                    Uri = ProbeUris.Fixed,
                    Name = "probe_fixed",
                    Title = $"Probe fixed resource ({ProbeTokens.ResourceLink})",
                    Description = "The fixed resource from probe Q4, linked from a tool result.",
                    MimeType = "text/markdown",
                },
            ],
        };

    [Authorize(Policy = WorkStatePolicies.Writer)]
    [McpServerTool(
        Name = "probe_writer_only",
        Title = "Probe: writer-scoped tool",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true)]
    [Description("""
        PROBE Q6 — does a reader token see this tool in tools/list at all, or does it only fail on
        call? Carries [Authorize(Policy = "workstate-writer")], honoured by AddAuthorizationFilters().
        A reader token calling it must be refused; whether it is also hidden from the list is the
        open question (docs/workstate/01-mcp-surface.md §6).
        """)]
    public static CallToolResult WriterOnly()
        => new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"probe_writer_only ok  {ProbeTokens.WriterOnly}  "
                         + "(Q6: you presented a token that satisfies workstate-writer)",
                },
            ],
        };
}
