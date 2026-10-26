using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using WorkState.Core.Board;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;

namespace WorkState.Mcp.Tools;

/// <summary>
/// The write half of doc 01 §3.2–§3.4. Same output contract as <see cref="WorkReadTools"/> — one
/// text block, no structured content — and the same description shape: what it does, when to reach
/// for it, and what to call next.
/// <para>
/// A refusal comes back as <c>isError: true</c> with the remedial text of doc 06 §6, because a tool
/// execution error is the one message a model is guaranteed to read and act on (doc 04 §3).
/// </para>
/// </summary>
[McpServerToolType]
public sealed class WorkWriteTools(
    WorkWriter writer,
    BoardReader reader,
    BoardQueries board,
    WorkspaceOptions workspace,
    CallerIdentity caller)
{
    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_status", Title = "Move: the guarded transition")]
    [Description("""
        Moves an item: open → ready → in_progress → review → done. in_progress takes a claim on the
        working tree; done is refused until every criterion is checked, an orchestrator verification
        exists, no child is open and no blocker finding targets it. Read the refusal — it names the
        call for each thing missing. Deferring is a move, not a status: it needs target: and creates
        a real open item there, so nothing is dropped silently.
        Next: after in_progress, work_verify; after done, work_next.
        """)]
    public async Task<CallToolResult> StatusAsync(
        [Description("The item, e.g. \"2.3\" or \"entity-store/2.3\".")]
        string @ref,
        [Description("ready | in_progress | blocked | review | done | cancelled | deferred")]
        string to,
        [Description("Required for blocked, cancelled, deferred and force.")]
        string? reason = null,
        [Description("Required for deferred: where the work goes, or \"backlog\".")]
        string? target = null,
        [Description("For done: accepted (default), reworked, replanned, escalated, skipped, wontfix, duplicate, obsolete.")]
        string? resolution = null,
        [Description("A commit sha to record as it closes.")]
        string? commit = null,
        [Description("Close over unmet conditions. Needs reason; leaves a permanent forced_transition.")]
        bool force = false,
        [Description("For in_progress: your worktree path or branch; one writer per tree.")]
        string? tree = null,
        CancellationToken cancellationToken = default)
        => await WriteAsync(
            "work_status",
            @ref,
            id => writer.StatusAsync(
                id, @ref, to, caller.Actor,
                new StatusArgs(reason, target, resolution, commit, force, tree),
                cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerTool(Name = "work_verify", Title = "Evidence: what a command actually printed")]
    [Description("""
        Records what a command actually printed against an item. run_by comes from your token: a
        worker's evidence is recorded as a worker's and can never satisfy that item's own done
        guard, which needs an orchestrator run.
        Next: work_status(ref, "done") if it passed and you are the orchestrator, then work_next.
        """)]
    public async Task<CallToolResult> VerifyAsync(
        [Description("The item this evidence belongs to.")]
        string @ref,
        [Description("The line it printed, e.g. \"Passed! Failed: 0, Passed: 91\" — never an exit code.")]
        string result_line,
        [Description("The command. Defaults to the item's verify_cmd.")]
        string? command = null,
        [Description("Record red runs too — an audit looks for them.")]
        bool passed = true,
        [Description("orchestrator | worker | ci | human. A worker token is always worker.")]
        string? run_by = null,
        [Description("JSON counts, e.g. {\"passed\":187,\"failed\":0}.")]
        string? counts = null,
        [Description("What you reverted to prove it could fail, and how many failed.")]
        string? red_check = null,
        CancellationToken cancellationToken = default)
        => await WriteAsync(
            "work_verify",
            @ref,
            id => writer.VerifyAsync(
                id, @ref, caller.Actor, command, result_line, passed, run_by, counts, red_check,
                cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerTool(Name = "work_report", Title = "Hand back: the worker report as a schema")]
    [Description("""
        Hands a task back as a schema rather than prose. Each line of assumptions[] becomes a real
        assumption row that stays in work_brief until someone resolves it.
        Next: work_verify — a report is a claim, not evidence.
        """)]
    public async Task<CallToolResult> ReportAsync(
        [Description("The item you worked on.")]
        string @ref,
        [Description("done | partial | blocked")]
        string status,
        [Description("Paths you changed.")]
        string[]? files = null,
        [Description("The command you ran and the line it printed.")]
        string? verify = null,
        [Description("What you reverted to prove it could fail, and how many failed.")]
        string? red_check = null,
        [Description("What you did differently from the brief, and why.")]
        string? deviations = null,
        [Description("Decisions taken without conclusive evidence. Each becomes an assumption.")]
        string[]? assumptions = null,
        [Description("What might bite later.")]
        string? risks = null,
        [Description("What you did not finish, and why.")]
        string? not_done = null,
        CancellationToken cancellationToken = default)
        => await WriteAsync(
            "work_report",
            @ref,
            id => writer.ReportAsync(
                id, @ref, caller.Actor,
                new ReportArgs(status, files, verify, red_check, deviations, assumptions, risks, not_done),
                cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_create", Title = "Plan: items, in a batch")]
    [Description("""
        Creates items in one call, because a decomposition is one decision. Kinds: project, feature,
        run, phase, task, subtask, finding, followup, question, incident, spike.
        It refuses a task with no verify_cmd or no done-criterion — a brief that omits either cannot
        be closed honestly, so it cannot be written.
        Next: work_link if any depends on another, then work_next.
        """)]
    public async Task<CallToolResult> CreateAsync(
        [Description("Each: kind, title; a task also needs verify_cmd and criteria[]; a project takes repo:{remote, root}.")]
        NewItemArg[] items,
        CancellationToken cancellationToken = default)
    {
        return Result(await Guarded("work_create(items:[…])", async () =>
        {
            var workspaceId = await writer.EnsureWorkspaceAsync(workspace.Slug, cancellationToken);
            return await writer.CreateAsync(workspaceId, caller.Actor, items ?? [], cancellationToken);
        }));
    }

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_update", Title = "Edit: fields and done-criteria")]
    [Description("""
        Patches an item's fields and its done-criteria. A criterion is addressed by the number a
        board row prints; one with a text and no id is appended.
        Next: work_status(ref, "done") when the criteria are met.
        """)]
    public async Task<CallToolResult> UpdateAsync(
        [Description("The item to patch.")]
        string @ref,
        [Description("[{id:\"c2\", checked:true, evidence:\"…\"}]")]
        CriterionArg[]? criteria = null,
        [Description("New title.")] string? title = null,
        [Description("What this is for, not what it does.")] string? intent = null,
        [Description("The command it will be judged by. It must be able to fail.")]
        string? verify_cmd = null,
        [Description("Any non-empty set marks the item review-required.")]
        string[]? risk_tags = null,
        [Description("Higher sorts earlier on the frontier. -9..9.")] int? priority = null,
        [Description("The revision you read. A mismatch is reported, not overwritten.")]
        int? expected_revision = null,
        [Description("A project only: {remote, root} of one more repo it holds.")]
        RepoArg? repo = null,
        [Description("A project only: true hides it from lists, false restores it.")]
        bool? archived = null,
        CancellationToken cancellationToken = default)
        => await WriteAsync(
            "work_update",
            @ref,
            id => writer.UpdateAsync(
                id, caller.Actor, @ref,
                new UpdateArgs(title, intent, verify_cmd, risk_tags, priority, criteria, expected_revision, repo, archived),
                cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_move", Title = "Reshape: reparent or reorder")]
    [Description("""
        Reparents an item, or slots it before or after a sibling. Ordering is fractional, so
        inserting 2.1b between 2.1 and 2.2 rewrites one row and renumbers nothing.
        Next: work_tree(scope) to confirm the shape.
        """)]
    public async Task<CallToolResult> MoveAsync(
        [Description("The item to move.")] string @ref,
        [Description("The new parent. Omit when using before/after.")] string? parent = null,
        [Description("Sit immediately before this sibling.")] string? before = null,
        [Description("Sit immediately after this sibling.")] string? after = null,
        CancellationToken cancellationToken = default)
        => await WriteAsync(
            "work_move",
            @ref,
            id => writer.MoveAsync(id, caller.Actor, @ref, parent, before, after, cancellationToken),
            cancellationToken);

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_link", Title = "Order: dependencies and lineage")]
    [Description("""
        Records an edge: blocks (the frontier reads it), supersedes (which marks the target
        superseded in the same transaction), duplicates, derived_from, verifies. A blocks cycle is
        refused by the database, naming the edge that would close it.
        Next: work_next — the frontier has just changed.
        """)]
    public async Task<CallToolResult> LinkAsync(
        [Description("For blocks, the item that must finish first.")]
        string from,
        [Description("The ref the edge points to.")] string to,
        [Description("blocks | supersedes | duplicates | derived_from | verifies")] string type,
        [Description("Delete this edge instead of creating it.")] bool remove = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);
        if (workspaceId is not { } id)
        {
            return Result(WriteResult.No(Empty("work_link")));
        }

        var result = await Guarded(
            $"work_link(from:\"{from}\", to:\"{to}\", type:\"{type}\")",
            () => writer.LinkAsync(id, caller.Actor, from, to, type, remove, cancellationToken));
        return await AttendAsync(result, to, cancellationToken);
    }

    [Authorize(Policy = WorkStatePolicies.Worker)]
    [McpServerTool(Name = "work_note", Title = "Carry forward: a fact the next agent needs")]
    [Description("""
        Records something the next agent must not have to rediscover. Addressed with to:, it renders
        into that item's brief and is marked consumed there — delivered exactly once.
        Do not paste code or diffs; record the sha and the path.
        Next: nothing. It is a side effect, not a step.
        """)]
    public async Task<CallToolResult> NoteAsync(
        [Description("carry_forward | do_not_rederive | correction | incident | environment | decision | observation")]
        string kind,
        [Description("The note, in one or two lines.")] string body,
        [Description("A future item's ref, e.g. \"3.1\".")]
        string? to = null,
        [Description("The item this came out of.")] string? from = null,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);
        if (workspaceId is not { } id)
        {
            return Result(WriteResult.No(Empty("work_note")));
        }

        return Result(await Guarded(
            $"work_note(kind:\"{kind}\", …)",
            () => writer.NoteAsync(id, caller.Actor, kind, body, to, from, cancellationToken)));
    }

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_assume", Title = "Record or settle an assumption")]
    [Description("""
        Opens an assumption, or resolves one. An open assumption sits in work_brief; one past its
        revisit point moves into attention. Resolving clears its reversal cost.
        Next: nothing, unless resolving it unblocks an item — then work_next.
        """)]
    public async Task<CallToolResult> AssumeAsync(
        [Description("On resolve the assumption's ref, e.g. \"A3\"; on create the item it is scoped to.")]
        string @ref,
        [Description("What is being assumed. Omit when resolving.")] string? statement = null,
        [Description("On create: asserted | reasoned | code_read | probed | measured. On resolve: what settled it.")]
        string? evidence = null,
        [Description("trivial | low | medium | high | irreversible — the cost of being wrong.")]
        string? reversal = null,
        [Description("The item whose closure triggers a revisit.")] string? revisit_when = null,
        [Description("confirmed | disproven | obsolete")] string? resolve = null,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);
        if (workspaceId is not { } id)
        {
            return Result(WriteResult.No(Empty("work_assume")));
        }

        return Result(await Guarded(
            $"work_assume(ref:\"{@ref}\", …)",
            () => writer.AssumeAsync(
                id, caller.Actor, @ref, statement, evidence, reversal, revisit_when, resolve, evidence,
                cancellationToken)));
    }

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_constrain", Title = "Bind: a rule every brief in scope prints")]
    [Description("""
        Records a rule that binds, or retires one by ref. It renders in the BINDING block of every
        brief in scope, as C1, C2, … — the ref is how you cite, question or retire it later.
        A red_line needs route: — what to do instead, or who to ask. A prohibition with no way out
        is a trap, and "stop and report it" is a route.
        Retiring leaves the row with active = false, so the audit keeps what used to bind.
        Next: nothing. It is a side effect, not a step.
        """)]
    public async Task<CallToolResult> ConstrainAsync(
        [Description("On retire, the rule's ref, e.g. \"C4\". Omit when creating.")]
        string? @ref = null,
        [Description("red_line | stack_fact | command | domain_rule | generated_code")]
        string? kind = null,
        [Description("The rule, as the brief will print it.")] string? text = null,
        [Description("The way out. Required for a red_line.")] string? route = null,
        [Description("An item's ref. Omitted, it binds the whole workspace.")]
        string? scope = null,
        [Description("risk tags this covers, matched against an item's risk_tags.")]
        string[]? covers_risk = null,
        [Description("binding (default) or advisory.")] string? severity = null,
        [Description("The item that discovered it was needed.")] string? from = null,
        [Description("Retire the rule this ref names instead of creating one.")]
        bool retire = false,
        [Description("Required for retire: why it no longer binds.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);
        if (workspaceId is not { } id)
        {
            return Result(WriteResult.No(Empty("work_constrain")));
        }

        return Result(await Guarded(
            $"work_constrain(kind:\"{kind}\", …)",
            () => writer.ConstrainAsync(
                id,
                caller.Actor,
                @ref,
                new ConstrainArgs(kind, text, route, scope, covers_risk, severity, from, retire, reason),
                cancellationToken)));
    }

    [Authorize(Policy = WorkStatePolicies.Orchestrator)]
    [McpServerTool(Name = "work_finding", Title = "Judge: what a review or gate found")]
    [Description("""
        Records what a review, gate or audit found as real items against their targets. A blocker
        finding blocks its target's done transition until it is closed or deferred — the gate result
        that used to dissolve into prose.
        Next: work_status(ref, "in_progress") on the target, or deferred on the finding.
        """)]
    public async Task<CallToolResult> FindingAsync(
        [Description("Each: target, title, severity blocker|minor, source review|gate|plan_audit|self.")]
        NewFinding[] findings,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);
        if (workspaceId is not { } id)
        {
            return Result(WriteResult.No(Empty("work_finding")));
        }

        return Result(await Guarded(
            "work_finding(findings:[…])",
            () => writer.FindingAsync(id, caller.Actor, findings ?? [], cancellationToken)));
    }

    [Authorize(Policy = WorkStatePolicies.Importer)]
    [McpServerTool(Name = "work_import", Title = "Import: what a repo's planning material says is still true")]
    [Description("""
        Writes one batch of records an agent read from a repo's plans, docs and rules and judged
        still true of the current code. Kinds: run, phase, task, followup, note, assumption, rule.
        Each carries its source and why; re-importing a source updates its record, never one moved
        since. Status is open, ready or blocked — nothing imported is done or verified.
        Next: dry_run:true first and show the human; then without it; then work_tree.
        """)]
    public async Task<CallToolResult> ImportAsync(
        [Description("The repo's project ref (work_brief(repo:) names it); it must be bound to the repo.")]
        string project,
        [Description("Each: source:{path, anchor?}, kind, why, title (items) or body (note/assumption/rule); "
                     + "optional ref, parent (existing, or created above in this batch), intent, status, criteria[], "
                     + "verify_cmd, route (a rule with one is a red_line).")]
        ImportItemArg[] items,
        [Description("Validate and show what would be created, updated or skipped; write nothing.")]
        bool dry_run = false,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);
        if (workspaceId is not { } id)
        {
            return Result(WriteResult.No(Empty("work_import")));
        }

        return Result(await Guarded(
            $"work_import(project:\"{project}\", items:[…])",
            () => writer.ImportAsync(id, caller.Actor, project, items ?? [], dry_run, cancellationToken)));
    }

    private async Task<CallToolResult> WriteAsync(
        string tool,
        string reference,
        Func<Guid, Task<WriteResult>> write,
        CancellationToken cancellationToken)
    {
        var workspaceId = await board.WorkspaceAsync(workspace.Slug, cancellationToken);
        if (workspaceId is not { } id)
        {
            return Result(WriteResult.No(Empty("this call")));
        }

        return await AttendAsync(
            await Guarded($"{tool}(ref:\"{reference}\", …)", () => write(id)),
            reference,
            cancellationToken);
    }

    /// <summary>
    /// doc 04 §3 at the transport: no write tool answers "An error occurred". The writer catches the
    /// guards it can provoke on purpose; this catches the ones only a concurrent writer can.
    /// </summary>
    private static async Task<WriteResult> Guarded(string call, Func<Task<WriteResult>> write)
    {
        try
        {
            return await write();
        }
        catch (PostgresException refused)
        {
            return WriteResult.No(WorkWriter.Uncaught(refused, call));
        }
    }

    /// <summary>
    /// doc 06 §5 — the attention block rides on a successful write too. It is left off a refusal on
    /// purpose: doc 06 §6 ends a refusal with the override and its cost, and a nag block after that
    /// competes with the remedy at the one moment the agent has to read the remedy.
    /// </summary>
    private async Task<CallToolResult> AttendAsync(
        WriteResult result,
        string reference,
        CancellationToken cancellationToken)
    {
        if (result.Refused)
        {
            return Result(result);
        }

        var scope = await reader.ResolveAsync(workspace.Slug, reference, cancellationToken);

        return scope is ScopeResult.Found found
            ? Result(result with
            {
                Text = Attention.Append(result.Text, await reader.AttentionAsync(found, cancellationToken)),
            })
            : Result(result);
    }

    private static string Empty(string call)
        => Refusal.Blocked(
            $"{call} — this server holds no work yet.",
            "there is no workspace to write into",
            "work_create(items:[{kind:\"project\", ref:\"<slug>\", title:\"<what the repo is>\"}]) "
            + "creates one",
            "work_brief() for the onboarding payload");

    private static CallToolResult Result(WriteResult result) => new()
    {
        Content = [new TextContentBlock { Text = result.Text }],
        IsError = result.Refused,
    };
}
