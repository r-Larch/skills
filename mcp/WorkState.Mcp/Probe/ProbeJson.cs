using System.Text.Json;

namespace WorkState.Mcp.Probe;

internal static class ProbeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
