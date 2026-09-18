namespace WorkState.Abstractions.Probe;

/// <summary>
/// Greppable markers, one per probe question. Each primitive emits exactly one of these, so a
/// transcript can be searched for the marker to prove that channel reached the model.
/// The values are load-bearing: <c>mcp/PROBE.md</c> tells the reader to grep for them literally.
/// </summary>
public static class ProbeTokens
{
    /// <summary>Emitted by <c>McpServerOptions.ServerInstructions</c>.</summary>
    public const string ServerInstructions = "WSPROBE-SI-7F3A9C";

    /// <summary>Emitted in <c>probe_echo</c>'s text content block.</summary>
    public const string EchoText = "WSPROBE-TEXT-A1B2C3";

    /// <summary>Emitted in <c>probe_echo</c>'s structuredContent, and nowhere else.</summary>
    public const string EchoStructured = "WSPROBE-JSON-D4E5F6";

    /// <summary>Emitted in <c>probe_link</c>'s resource link title.</summary>
    public const string ResourceLink = "WSPROBE-LINK-11A2B3";

    /// <summary>Emitted in the rendered <c>probe_prompt</c> message.</summary>
    public const string Prompt = "WSPROBE-PROMPT-22C4D5";

    /// <summary>Emitted in the body of the fixed resource <c>work://probe/fixed</c>.</summary>
    public const string FixedResource = "WSPROBE-RES-FIXED-33E6F7";

    /// <summary>Emitted in the body of the templated resource <c>work://probe/{id}</c>.</summary>
    public const string TemplateResource = "WSPROBE-RES-TMPL-44A8B9";

    /// <summary>Emitted as a completion candidate for <c>{id}</c>.</summary>
    public const string Completion = "WSPROBE-COMPLETE-55CA1B";

    /// <summary>Emitted by <c>probe_writer_only</c>, which only a writer token may call.</summary>
    public const string WriterOnly = "WSPROBE-WRITER-66DC2E";

    // Round 2. Round 1 established that structuredContent REPLACES content rather than accompanying
    // it, which invalidates docs/workstate/01-mcp-surface.md §2. One marker per candidate remedy:
    // whichever reaches the model is the output contract the real tools must use.

    /// <summary>Remedy A — text only, no structuredContent at all.</summary>
    public const string RemedyTextOnly = "WSPROBE-RA-TEXTONLY-77E1D2";

    /// <summary>Remedy B — the compact rendering carried INSIDE structuredContent, as `render`.</summary>
    public const string RemedyRenderInJson = "WSPROBE-RB-RENDER-88F3C4";

    /// <summary>Remedy C — the same rendering duplicated into both halves.</summary>
    public const string RemedyDuplicated = "WSPROBE-RC-DUPE-99A5B6";
}
