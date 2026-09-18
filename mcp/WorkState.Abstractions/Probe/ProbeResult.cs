using System.ComponentModel;

namespace WorkState.Abstractions.Probe;

/// <summary>
/// The structured half of <c>probe_echo</c>'s result. It exists only to give the probe an
/// <c>outputSchema</c> worth looking at; no real tool returns this shape.
/// </summary>
public sealed record ProbeResult
{
    [Description("The string the caller passed in, echoed back unchanged.")]
    public required string Echoed { get; init; }

    [Description("Character length of the echoed string.")]
    public required int Length { get; init; }

    [Description("Server clock at the moment the tool ran, ISO-8601 UTC.")]
    public required string ServerTimeUtc { get; init; }

    [Description("Which probe question this result is evidence for.")]
    public required string Answers { get; init; }

    [Description("Distinctive token a transcript can be grepped for to prove structuredContent reached the model.")]
    public required string ProbeToken { get; init; }
}
