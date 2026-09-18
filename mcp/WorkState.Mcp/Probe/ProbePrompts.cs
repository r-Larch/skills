using System.ComponentModel;
using ModelContextProtocol.Server;
using WorkState.Abstractions.Probe;

namespace WorkState.Mcp.Probe;

[McpServerPromptType]
public static class ProbePrompts
{
    [McpServerPrompt(Name = "probe_prompt", Title = "Probe: server prompt with one required argument")]
    [Description("""
        PROBE Q3 — do server prompts appear in this client at all, and how are they invoked?
        Takes one required argument, `topic`, and renders it back inside a marked block.
        """)]
    public static string Render(
        [Description("Required. Anything; it is echoed into the rendered prompt.")] string topic)
        => $"""
            {ProbeTokens.Prompt}

            If you are reading this, server prompts reached the model in this client.

            topic = {topic}

            Report to whoever is running this probe: how you were offered this prompt (slash command,
            @-mention, resource picker, or not at all), and whether `topic` was collected from you.
            """;
}
