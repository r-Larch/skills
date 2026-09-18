namespace WorkState.Mcp;

/// <summary>
/// Authorization policy names. Shared with the host, which registers them, and with
/// <c>[Authorize]</c> attributes on tools, which <c>AddAuthorizationFilters()</c> honours.
/// </summary>
public static class WorkStatePolicies
{
    public const string Reader = "workstate-reader";
    public const string Writer = "workstate-writer";
}
