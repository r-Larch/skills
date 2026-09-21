namespace WorkState.Mcp;

/// <summary>
/// Authorization policy names. Shared with the host, which registers them, and with
/// <c>[Authorize]</c> attributes on tools, which <c>AddAuthorizationFilters()</c> honours.
/// </summary>
public static class WorkStatePolicies
{
    /// <summary>
    /// doc 01 §6. Three profiles, nested: a controller token also satisfies orchestrator and
    /// worker. Because <c>[Authorize]</c> <em>hides</em> a tool rather than refusing it (measured,
    /// PROBE Q6), crossing one of these boundaries produces no error the agent can read — which is
    /// why <see cref="WorkStateServerInstructions"/> states the role instead.
    /// </summary>
    public const string Worker = "worker";

    public const string Orchestrator = "orchestrator";
    public const string Controller = "controller";

    /// <summary>Any recognised token. <c>/mcp</c> requires it, so an unknown token never reaches a tool.</summary>
    public const string Any = "workstate-any";
}
