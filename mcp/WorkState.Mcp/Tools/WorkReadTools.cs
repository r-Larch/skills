using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WorkState.Core.Board;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;

namespace WorkState.Mcp.Tools;

/// <summary>
/// The eight read tools of doc 01 §3.1, in declaration order — <c>tools/list</c> ordering is
/// deterministic, which is what lets the client's prompt cache hold across a whole run.
/// <para>
/// Every one returns <b>one</b> <see cref="TextContentBlock"/> and no <c>StructuredContent</c>.
/// That is not a style choice: <c>structuredContent</c> <em>replaces</em> the text block in this
/// client (PROBE §6), so sending it would make the rendering — and with it the attention block —
/// invisible to the model. For the same reason no tool here declares an output schema.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class WorkReadTools(
    BoardReader reader,
    WorkspaceOptions workspace,
    Housekeeping housekeeping,
    CallerIdentity caller)
{
    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerTool(Name = "work_brief", Title = "Orient: the recovery payload", ReadOnly = true)]
    [Description("""
        The recovery payload for a scope, generated so it cannot be stale: goal, where the run
        stands, the literal next action, the constraints that bind you, open assumptions, notes
        addressed to this work, and the last verification recorded.
        Call it FIRST in every session, and again after a compaction — nothing else needs reading
        to orient. On an empty server it returns onboarding instead: what to create, and how.
        Next: do what its own NEXT line says — usually work_get(refs:[...]) and then
        work_status(ref, "in_progress").
        """)]
    public async Task<CallToolResult> BriefAsync(
        [Description("A run or item ref, e.g. \"entity-store\" or \"2.3\". Omit for your repo's project; with several projects and no repo, a list to choose from.")]
        string? scope = null,
        [Description("\"resume\" (default) for the session payload, or \"worker\" to add the task's own criteria, verification and report format.")]
        string? @for = null,
        [Description("Without scope: your repo, {remote: git remote get-url origin, root: main checkout root}, opens its project.")]
        RepoArg? repo = null,
        CancellationToken cancellationToken = default)
    {
        var byRepo = string.IsNullOrWhiteSpace(scope)
            ? await reader.ResolveRepoAsync(workspace.Slug, repo?.Keys() ?? [], caller.Actor.SessionId, cancellationToken)
            : null;

        var resolved = byRepo ?? (string.IsNullOrWhiteSpace(scope)
            ? await reader.ResolveUnscopedBriefAsync(workspace.Slug, cancellationToken)
            : await reader.ResolveAsync(workspace.Slug, scope, cancellationToken));
        if (resolved is not ScopeResult.Found found)
        {
            return Text(ScopeExplain.Text(resolved, "work_brief", scope));
        }

        // The sweep of doc 07 §5 and the note consumption of doc 01 §3.4 both live in
        // BriefSessionAsync, because GET /api/v1/items/{id}/brief must perform the identical writes in
        // the identical order or the two surfaces return different bytes for the same scope.
        var brief = await reader.BriefSessionAsync(
            found,
            @for ?? "resume",
            housekeeping,
            cancellationToken,
            signed: caller.Actor.SessionId is not null,
            keyed: caller.ClientKey is not null);

        return Text(string.IsNullOrWhiteSpace(scope) && byRepo is null ? WithoutRepo(brief) : brief);
    }

    /// <summary>
    /// The busiest run of the whole workspace is a guess once it holds several projects, so the brief
    /// that made it says how to stop guessing — under the header, after the session line.
    /// </summary>
    private static string WithoutRepo(string brief)
    {
        var lines = brief.Split('\n').ToList();
        var at = lines.Count > 1 && lines[1].StartsWith("session  ", StringComparison.Ordinal) ? 2 : 1;
        lines.Insert(
            Math.Min(at, lines.Count),
            "repo     not given — work_brief(repo:{remote, root}) opens your repo's project instead");
        return string.Join("\n", lines);
    }

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_next", Title = "Act: the ready frontier", ReadOnly = true)]
    [Description("""
        The ready frontier: items whose blocking dependencies are all done and whose children are
        all closed, in plan order, each with the verification command it will be judged by.
        Call it after work_brief, and again after closing anything — the frontier moves.
        Next: work_get(refs:["<ref>"]) for the full brief, then work_status(ref, "in_progress").
        """)]
    public async Task<CallToolResult> NextAsync(
        [Description("A run or item ref to look under. Omit for the busiest run.")]
        string? scope = null,
        [Description("How many rows to return. Default 10.")]
        int limit = 10,
        [Description("Restrict to one item kind, e.g. \"task\" or \"finding\".")]
        string? kind = null,
        CancellationToken cancellationToken = default)
        => await WithScopeAsync(
            "work_next",
            scope,
            found => reader.NextAsync(found, Clamp(limit, 1, 50), kind, cancellationToken),
            cancellationToken,
            sweep: true);

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_tree", Title = "Orient: the shape", ReadOnly = true)]
    [Description("""
        The shape of a scope: an indented tree with a done/total rollup on
        every container and blocking edges marked on the rows they hold up.
        Reach for it to see where a ref sits or how much of a phase is left — not to decide what to
        do next, which is work_next's job.
        Next: work_next(scope) to act, or work_threads(scope) for everything unclosed.
        """)]
    public async Task<CallToolResult> TreeAsync(
        [Description("A run or item ref to render. Omit for the busiest run.")]
        string? scope = null,
        [Description("Levels below the scope to show. Default 3.")]
        int depth = 3,
        [Description("Comma-separated statuses to keep, e.g. \"open,in_progress,blocked\". Omit for all.")]
        string? status = null,
        [Description("Show the done/total rollup on containers. Default true.")]
        bool includeCounts = true,
        CancellationToken cancellationToken = default)
        => await WithScopeAsync(
            "work_tree",
            scope,
            found => reader.TreeAsync(found, Clamp(depth, 1, 8), Split(status), includeCounts, cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerTool(Name = "work_get", Title = "Read: one or more items in full", ReadOnly = true)]
    [Description("""
        Full detail for one or more refs: intent, done-criteria with their check state, the
        verifications recorded against them, the links in and out, and the work:// URIs of the item
        and of its worker brief.
        Reach for it before starting an item, or whenever a board row is not enough. The brief is
        never inlined — read the printed work://item/<ref>/brief URI with ReadMcpResourceTool.
        Next: work_status(ref, "in_progress") to start it, or work_brief(scope:"<ref>",
        for:"worker") to hand it to someone else.
        """)]
    public async Task<CallToolResult> GetAsync(
        [Description("One or more refs, e.g. [\"2.3\", \"F-07\"]. Qualify across runs: \"entity-store/2.3\".")]
        string[] refs,
        [Description("Comma-separated extras: \"deps\" for the links in and out.")]
        string? include = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await reader.ResolveDefaultAsync(workspace.Slug, null, caller.Actor.SessionId, cancellationToken);
        if (resolved is not ScopeResult.Found found)
        {
            return Text(ScopeExplain.Text(resolved, "work_get", null));
        }

        var body = await reader.GetAsync(found, refs ?? [], Split(include), cancellationToken);
        var attended = await reader.AttentionScopeAsync(found, refs?.FirstOrDefault(), cancellationToken);
        return Text(Attention.Append(body, await reader.AttentionAsync(attended, cancellationToken)));
    }

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerTool(Name = "work_search", Title = "Find: full text over items", ReadOnly = true)]
    [Description("""
        Full-text search across item titles and intents, returning a ref and a snippet for each
        hit.
        Reach for it when you know a phrase but not a ref — it is what replaces grepping a run
        directory, and it is how you avoid inventing a ref.
        Next: work_get(refs:["<ref>"]) on a hit.
        """)]
    public async Task<CallToolResult> SearchAsync(
        [Description("What to look for. Ordinary words; phrases in quotes work.")]
        string q,
        [Description("How many hits. Default 10.")]
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var resolved = await reader.ResolveDefaultAsync(workspace.Slug, null, caller.Actor.SessionId, cancellationToken);
        if (resolved is not ScopeResult.Found found)
        {
            return Text(ScopeExplain.Text(resolved, "work_search", null));
        }

        var body = await reader.SearchAsync(found.WorkspaceId, q ?? string.Empty, Clamp(limit, 1, 50), cancellationToken);
        return Text(Attention.Append(body, await reader.AttentionAsync(found, cancellationToken)));
    }

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_threads", Title = "Close out: everything unclosed that is not a task", ReadOnly = true)]
    [Description("""
        Everything unclosed that is not a task: open assumptions, findings, follow-ups, unconsumed
        notes, items in_progress with no live claim, and dependencies waiting on work that will
        never arrive. Every line ends in the call that closes it.
        Reach for it before closing a phase and at every handover — this is the list that decides
        whether a run ends clean or with 84% of its checkboxes open.
        Next: the call named on each line — work_assume, work_status, or work_brief(for:"worker").
        """)]
    public async Task<CallToolResult> ThreadsAsync(
        [Description("A run or item ref. Omit for the busiest run.")]
        string? scope = null,
        [Description("Comma-separated: assumptions, findings, followups, notes, stale, deps. Omit for all.")]
        string? include = null,
        CancellationToken cancellationToken = default)
        => await WithScopeAsync(
            "work_threads",
            scope,
            found => reader.ThreadsAsync(found, Split(include), cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_audit", Title = "Judge: the mechanical half of a plan audit", ReadOnly = true)]
    [Description("""
        The checks a query can make: verification commands this workspace has never recorded red,
        items closed without an orchestrator verification, dependency inversions, superseded items
        open work still points at, risk tags no active constraint covers, and binding rules with no
        route out. Each finding prints the call that fixes it.
        Run it before a gate or a handover, then treat its results as established and spend your own
        budget on shape and on load-bearing facts, which it cannot check.
        Next: work_update / work_link to fix what it found; work_note(kind:"decision") for what you
        are deliberately not fixing.
        """)]
    public async Task<CallToolResult> AuditAsync(
        [Description("A run or item ref to audit. Omit for the busiest run.")]
        string? scope = null,
        CancellationToken cancellationToken = default)
        => await WithScopeAsync(
            "work_audit",
            scope,
            found => reader.AuditAsync(found, cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_history", Title = "Recover: what happened while you were away", ReadOnly = true)]
    [Description("""
        The append-only event stream, newest first and cursor-paged by seq: transitions,
        verifications, forced closures, imports.
        Reach for it after a compaction, or when picking up a run someone else was driving — it is
        the answer to "what changed since I last looked".
        Next: work_get(refs:["<ref>"]) on anything surprising.
        """)]
    public async Task<CallToolResult> HistoryAsync(
        [Description("Narrow to one item and its subtree. Omit for the whole workspace.")]
        string? scope = null,
        [Description("Only events after this seq. Use the cursor printed by the previous call.")]
        long sinceSeq = 0,
        [Description("How many events. Default 20.")]
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var resolved = await reader.ResolveDefaultAsync(workspace.Slug, scope, caller.Actor.SessionId, cancellationToken);
        if (resolved is not ScopeResult.Found found)
        {
            return Text(ScopeExplain.Text(resolved, "work_history", scope));
        }

        var body = await reader.HistoryAsync(
            found.WorkspaceId,
            scope is { Length: > 0 } ? found : null,
            sinceSeq,
            Clamp(limit, 1, 100),
            cancellationToken);

        return Text(Attention.Append(body, await reader.AttentionAsync(found, cancellationToken)));
    }

    private async Task<CallToolResult> WithScopeAsync(
        string call,
        string? scope,
        Func<ScopeResult.Found, Task<string>> render,
        CancellationToken cancellationToken,
        bool sweep = false)
    {
        var resolved = await reader.ResolveDefaultAsync(workspace.Slug, scope, caller.Actor.SessionId, cancellationToken);
        if (resolved is not ScopeResult.Found found)
        {
            return Text(ScopeExplain.Text(resolved, call, scope));
        }

        if (sweep)
        {
            await housekeeping.SweepAsync(found.WorkspaceId, cancellationToken);
        }

        var body = await render(found);
        return Text(Attention.Append(body, await reader.AttentionAsync(found, cancellationToken)));
    }

    private static CallToolResult Text(string body)
        => new() { Content = [new TextContentBlock { Text = body }] };

    private static int Clamp(int value, int low, int high) => Math.Clamp(value, low, high);

    private static string[] Split(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(s => s.ToLowerInvariant())];
}
