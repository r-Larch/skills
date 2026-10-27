using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;
using WorkState.Core.Board;
using WorkState.Core.Rendering;

namespace WorkState.Mcp.Resources;

/// <summary>
/// doc 01 §8, as measured. The five <b>fixed</b> projections are listed and readable, so they are
/// the discoverable surface. The item and attention templates are registered as well because tools print
/// exact <c>work://item/&lt;ref&gt;</c> URIs and a template read of a known URI works — but a
/// template is <em>not</em> discoverable in this client (PROBE Q5), so nothing may rely on the
/// model composing one for itself.
/// </summary>
[McpServerResourceType]
public sealed class WorkResources(BoardReader reader, WorkspaceOptions workspace, CallerIdentity caller)
{
    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerResource(
        UriTemplate = WorkUris.Workspace,
        Name = "workstate_index",
        Title = "WorkState: where this workspace stands",
        MimeType = "text/markdown")]
    [Description("The active run: its shape, its rollups, and the attention block. The board, as a document.")]
    public async Task<string> IndexAsync(CancellationToken cancellationToken)
    {
        var scope = await DefaultAsync(cancellationToken);
        if (scope is not ScopeResult.Found found)
        {
            return BriefRenderer.Onboarding();
        }

        var tree = await reader.TreeAsync(found, 3, [], true, cancellationToken);
        return Attention.Append(tree, await reader.AttentionAsync(found, cancellationToken));
    }

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerResource(
        UriTemplate = WorkUris.WorkspaceBrief,
        Name = "workstate_brief",
        Title = "WorkState: the recovery payload",
        MimeType = "text/markdown")]
    [Description("The same payload work_brief returns, attachable with @ so it can be pinned into a session.")]
    public async Task<string> BriefAsync(CancellationToken cancellationToken)
    {
        // Resolved as a bare work_brief() is: the session's project, else a chooser among several.
        var scope = await reader.ResolveRepoAsync(workspace.Slug, [], caller.Actor.SessionId, cancellationToken)
                    ?? await reader.ResolveUnscopedBriefAsync(workspace.Slug, cancellationToken);
        return scope is ScopeResult.Found found
            ? await reader.BriefAsync(found, "resume", cancellationToken)
            : ScopeExplain.Text(scope, "work_brief", null);
    }

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerResource(
        UriTemplate = WorkUris.Conventions,
        Name = "workstate_conventions",
        Title = "WorkState: the rules that bind this run",
        MimeType = "text/markdown")]
    [Description("Every active constraint in scope, red lines first, each with its route out.")]
    public async Task<string> ConventionsAsync(CancellationToken cancellationToken)
    {
        var scope = await DefaultAsync(cancellationToken);
        return scope is ScopeResult.Found found
            ? await reader.ConventionsAsync(found, cancellationToken)
            : BriefRenderer.Onboarding();
    }

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerResource(
        UriTemplate = WorkUris.Handoff,
        Name = "workstate_handoff",
        Title = "WorkState: enough for a competent stranger to continue",
        MimeType = "text/markdown")]
    [Description("Brief, shape and every unclosed thread in one document — what a handover has to carry.")]
    public async Task<string> HandoffAsync(CancellationToken cancellationToken)
    {
        var scope = await DefaultAsync(cancellationToken);
        if (scope is not ScopeResult.Found found)
        {
            return BriefRenderer.Onboarding();
        }

        var brief = await reader.BriefAsync(found, "resume", cancellationToken);
        var tree = await reader.TreeAsync(found, 4, [], true, cancellationToken);
        var threads = await reader.ThreadsAsync(found, [], cancellationToken);
        return string.Join("\n\n", brief, tree, threads);
    }

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerResource(
        UriTemplate = WorkUris.Attention,
        Name = "workstate_attention",
        Title = "WorkState: everything that needs attention",
        MimeType = "text/markdown")]
    [Description("The active run's attention block, uncapped: every line, ranked, each with its call.")]
    public Task<string> AttentionAsync(CancellationToken cancellationToken)
        => AttentionOfAsync(null, cancellationToken);

    /// <summary>The route a capped attention block prints for the lines it cut (doc 06 §5).</summary>
    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerResource(
        UriTemplate = WorkUris.AttentionTemplate,
        Name = "workstate_attention_scope",
        Title = "WorkState: everything that needs attention under one scope",
        MimeType = "text/markdown")]
    [Description("One scope's attention block, uncapped. Reachable with the exact URI a capped block printed.")]
    public async Task<string> AttentionScopeAsync(
        [Description("The scope's ref, exactly as the capped block printed it.")] string scope,
        CancellationToken cancellationToken)
        => await AttentionOfAsync(scope, cancellationToken);

    private async Task<string> AttentionOfAsync(string? scope, CancellationToken cancellationToken)
    {
        var resolved = await reader.ResolveDefaultAsync(workspace.Slug, scope, caller.Actor.SessionId, cancellationToken);
        if (resolved is not ScopeResult.Found found)
        {
            return scope is null ? BriefRenderer.Onboarding() : ScopeExplain.Text(resolved, "work_threads", scope);
        }

        var lines = await reader.AttentionAsync(found, cancellationToken);
        return Attention.Full(((ScopedAttention)lines).Scope, lines);
    }

    private Task<ScopeResult> DefaultAsync(CancellationToken cancellationToken)
        => reader.ResolveDefaultAsync(workspace.Slug, null, caller.Actor.SessionId, cancellationToken);

    /// <summary>
    /// <c>{+ref}</c> rather than <c>{ref}</c>: reserved expansion lets the value carry a <c>/</c>, so
    /// the qualified ref a tool printed — <c>work://item/entity-store/3.1</c> — reads back instead of
    /// 404ing (ACCEPTANCE A-4). Both item templates match a URI ending in <c>/brief</c>, and which of
    /// them the server's linear template traversal reaches first is not a contract, so this one
    /// forwards rather than depending on the order.
    /// </summary>
    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerResource(
        UriTemplate = WorkUris.ItemTemplate,
        Name = "workstate_item",
        Title = "WorkState: one item",
        MimeType = "text/markdown")]
    [Description("One item in full. Reachable only with the exact URI a tool result printed.")]
    public async Task<string> ItemAsync(
        [Description("The item's ref, exactly as a tool result printed it.")] string @ref,
        CancellationToken cancellationToken)
    {
        if (@ref.EndsWith(WorkUris.BriefSuffix, StringComparison.Ordinal))
        {
            return await ItemBriefAsync(@ref[..^WorkUris.BriefSuffix.Length], cancellationToken);
        }

        var scope = await DefaultAsync(cancellationToken);
        return scope is ScopeResult.Found found
            ? await reader.GetAsync(found, [@ref], ["deps"], cancellationToken)
            : ScopeExplain.Text(scope, "work_get", @ref);
    }

    /// <summary>
    /// ACCEPTANCE A-2, the worst defect the cold-start transcript found: a ref that exists in more
    /// than one run resolved to neither, fell through to the cold-start branch, and answered
    /// <c>"this server holds no work yet"</c> about a store holding 267 tasks. Only an actually
    /// empty workspace is a cold start; an unresolved ref is a refusal that names the candidates.
    /// </summary>
    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerResource(
        UriTemplate = WorkUris.ItemBriefTemplate,
        Name = "workstate_item_brief",
        Title = "WorkState: one item's worker brief",
        MimeType = "text/markdown")]
    [Description("The brief for one item: context, constraints, criteria, verification and the report format.")]
    public async Task<string> ItemBriefAsync(
        [Description("The item's ref, exactly as a tool result printed it.")] string @ref,
        CancellationToken cancellationToken)
    {
        var scope = await reader.ResolveAsync(workspace.Slug, @ref, cancellationToken);
        return scope is ScopeResult.Found found
            ? await reader.BriefAsync(found, "worker", cancellationToken)
            : ScopeExplain.Text(scope, "work_brief", @ref);
    }
}
