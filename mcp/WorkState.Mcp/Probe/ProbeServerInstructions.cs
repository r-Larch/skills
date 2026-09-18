using WorkState.Abstractions.Probe;

namespace WorkState.Mcp.Probe;

/// <summary>
/// The text handed to <c>McpServerOptions.ServerInstructions</c>. In the real server this is the
/// ≤40-line budget of <c>docs/workstate/04-self-explaining.md</c> §1; here it is a probe, so its
/// only job is to be findable.
/// </summary>
public static class ProbeServerInstructions
{
    public static string Text { get; } =
        $"""
         === WORKSTATE PROBE — ServerInstructions channel ===
         {ProbeTokens.ServerInstructions}

         If you can read this line, ServerInstructions reached the model. That is the probe's most
         important single answer: docs/workstate/04-self-explaining.md §1 spends its whole budget here.

         This server is a PROBE, not the WorkState server. It has no state and no work_* tools.
         Six primitives, each labelled with the question it answers:
           probe_echo          Q1  text vs structuredContent, and outputSchema
           probe_link          Q2  resource_link from a tool result
           probe_prompt        Q3  server prompts
           work://probe/fixed  Q4  fixed resources
           {ProbeUris.Template}   Q5  resource templates + completion on the id variable
           probe_writer_only   Q6  [Authorize] — listed, or only refused on call?

         Asked to run the probe: call probe_echo, then probe_link, then read both resources, then
         probe_writer_only, and report for each WHAT YOU SAW, not what you expected.
         """;
}
