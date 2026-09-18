using System.ComponentModel;
using ModelContextProtocol.Server;
using WorkState.Abstractions.Probe;

namespace WorkState.Mcp.Probe;

[McpServerResourceType]
public static class ProbeResources
{
    [McpServerResource(
        UriTemplate = ProbeUris.Fixed,
        Name = "probe_fixed",
        Title = "Probe: fixed resource",
        MimeType = "text/markdown")]
    [Description("PROBE Q4 — do static resources appear in this client, and can they be attached?")]
    public static string Fixed()
        => $"""
            # {ProbeTokens.FixedResource}

            Fixed resource at `{ProbeUris.Fixed}`.

            If you can read this, `resources/list` surfaced a static resource and the client let it
            be read. That is probe question Q4 answered yes.
            """;

    [McpServerResource(
        UriTemplate = ProbeUris.Template,
        Name = "probe_templated",
        Title = "Probe: resource template with one variable",
        MimeType = "text/markdown")]
    [Description("PROBE Q5 — do resource templates appear, and is `{id}` completed by completions/complete?")]
    public static string Templated(
        [Description("Any id. Completion candidates are offered by the server's complete handler.")] string id)
        => $"""
            # {ProbeTokens.TemplateResource}

            Templated resource `{ProbeUris.Template}` read with id = `{id}`.

            If the client offered you a list of ids before you got here, `completions/complete`
            works too — look for {ProbeTokens.Completion} among the suggestions.
            """;
}
