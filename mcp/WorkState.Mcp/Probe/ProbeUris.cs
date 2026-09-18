namespace WorkState.Mcp.Probe;

/// <summary>The two resource URIs the probe publishes. The template shape is RFC 6570, per doc 01 §8.</summary>
public static class ProbeUris
{
    public const string Fixed = "work://probe/fixed";
    public const string Template = "work://probe/{id}";

    /// <summary>Candidate <c>{id}</c> values the completion handler offers for <see cref="Template"/>.</summary>
    public static readonly string[] TemplateIdCandidates = ["alpha", "beta", "gamma"];
}
