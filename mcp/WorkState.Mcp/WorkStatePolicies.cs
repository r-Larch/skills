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

    /// <summary>
    /// doc 10 rule 5. Not nested with the three above: a reader token satisfies none of them, so it
    /// reaches <c>GET /api/v1/*</c> and <c>/api/stream</c> and no MCP tool at all.
    /// </summary>
    public const string Reader = "reader";

    /// <summary>
    /// <c>work_import</c>. Not nested either: an orchestrator role plus the <c>import</c> grant claim,
    /// which only a minted token can carry, so no static token and no ungranted session ever lists it.
    /// </summary>
    public const string Importer = "importer";

    /// <summary>
    /// Any agent token. <c>/mcp</c> requires it, so an unknown token never reaches a tool — and
    /// neither does a reader token, which authenticates but holds no agent role.
    /// </summary>
    public const string Any = "workstate-any";
}
