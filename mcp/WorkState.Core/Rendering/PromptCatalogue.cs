using WorkState.Core.Domain;

namespace WorkState.Core.Rendering;

public enum PromptScope
{
    Project,
    Run,
    Item,
}

/// <summary>
/// What the catalogue's predicates and templates read about one scope. <see cref="Ref"/> is the
/// callable form: bare while unique in the workspace, else the qualified form that resolves alone.
/// </summary>
public sealed record PromptFacts(
    PromptScope Scope,
    string Ref,
    string Kind,
    string Title,
    ItemStatus Status,
    int Unclosed = 0,
    int Backlog = 0,
    string? BacklogRef = null,
    bool Finalizable = false,
    int DoneLeaves = 0);

/// <summary>One button: its label, the count it shows if any, and the whole prompt it copies.</summary>
public sealed record PromptAction(string Key, string Label, int? Count, string Text);

/// <summary>
/// doc 13 "Prompt actions" — the prompts the dashboard copies for a fresh session. The dashboard
/// never acts; each prompt starts the session with <c>work_brief</c> on the scope it was copied from,
/// and any prompt that closes, cancels, defers or archives carries <see cref="ProposeAndWait"/>.
/// The order of <see cref="Entries"/> is the order of the buttons.
/// </summary>
public static class PromptCatalogue
{
    public const string ProposeAndWait =
        "Propose first and wait for the human's yes: list every change you intend to make, and write nothing until the human answers yes.";

    public const string NoWrites = "Make no writes.";

    public sealed record Entry(
        string Key,
        string Label,
        PromptScope Scope,
        Func<PromptFacts, bool> Applies,
        Func<PromptFacts, int?> Count,
        Func<PromptFacts, string> Text,
        bool Writes);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("continue", "Continue", PromptScope.Project, _ => true, _ => null, Continue, Writes: false),
        new("status", "Status report", PromptScope.Project, _ => true, _ => null, Status, Writes: false),
        new("housekeeping", "Housekeeping", PromptScope.Project, f => f.Unclosed > 0, f => f.Unclosed, Housekeeping, Writes: true),
        new("triage", "Triage backlog", PromptScope.Project, f => f.Backlog > 0, f => f.Backlog, Triage, Writes: true),
        new("finalize", "Finalize", PromptScope.Project, f => f.Finalizable, _ => null, Finalize, Writes: true),
        new("resume", "Resume", PromptScope.Run, f => !Closed(f.Status), _ => null, Resume, Writes: false),
        new("audit", "Audit", PromptScope.Run, f => f.DoneLeaves > 0, _ => null, Audit, Writes: false),
        new("work", "Work on this", PromptScope.Item, f => Workable(f.Kind) && f.Status is ItemStatus.Open or ItemStatus.Ready, _ => null, Work, Writes: false),
        new("unblock", "Investigate block", PromptScope.Item, f => f.Status == ItemStatus.Blocked, _ => null, Unblock, Writes: false),
    ];

    /// <summary>The actions that apply to <paramref name="facts"/>, in catalogue order.</summary>
    public static IReadOnlyList<PromptAction> For(PromptFacts facts)
        => [.. Entries
            .Where(e => e.Scope == facts.Scope && e.Applies(facts))
            .Select(e => new PromptAction(e.Key, e.Label, e.Count(facts), e.Text(facts)))];

    public static bool Closed(ItemStatus status)
        => status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;

    private static bool Workable(string kind) => kind is "task" or "followup" or "subtask";

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static string Continue(PromptFacts f)
        => Lines(
            $"Continue in WorkState project {f.Ref} ({f.Title}): call work_brief(scope:\"{f.Ref}\") first, then do what its NEXT line says - resume the run it names, or start a run under the project.");

    private static string Status(PromptFacts f)
        => Lines(
            $"Report on WorkState project {f.Ref} ({f.Title}). {NoWrites}",
            $"Call work_brief(scope:\"{f.Ref}\") first, then work_tree(scope:\"{f.Ref}\") and work_threads(scope:\"{f.Ref}\").",
            "Report in three short parts:",
            "- what landed: what is done, with the evidence recorded for it;",
            "- what is open: each run, where it stands and what is next in it;",
            "- what needs the human: blocked items, open questions, decisions and assumptions waiting on a person.");

    private static string Housekeeping(PromptFacts f)
        => Lines(
            $"Housekeeping for WorkState project {f.Ref} ({f.Title}): {f.Unclosed} unclosed thread(s) to settle.",
            $"Call work_brief(scope:\"{f.Ref}\") first, then work_threads(scope:\"{f.Ref}\"). It lists every thread with the call that settles it; the attention block names expired claims.",
            "Go through each one and decide:",
            "- close only with evidence: run the check yourself, work_verify(ref:\"…\", result_line:\"…\", run_by:\"orchestrator\"), then work_status(ref:\"…\", to:\"done\");",
            "- defer as a move, never a drop: work_status(ref:\"…\", to:\"deferred\", target:\"backlog\", reason:\"…\");",
            "- cancel with a reason: work_status(ref:\"…\", to:\"cancelled\", reason:\"…\");",
            "- an assumption: settle it with the evidence, work_assume(ref:\"…\", resolve:\"confirmed\", evidence:\"…\");",
            "- an expired claim or a stale in_progress item: find out whether the work is finished, then close, defer or reopen it as above.",
            "Never force a transition.",
            ProposeAndWait);

    private static string Triage(PromptFacts f)
    {
        var tree = f.BacklogRef is { } backlog ? $"work_tree(scope:\"{backlog}\") for the backlog run, " : string.Empty;
        return Lines(
            $"Triage the backlog of WorkState project {f.Ref} ({f.Title}): {f.Backlog} open item(s) - the backlog run's open work and every open follow-up under the project.",
            $"Call work_brief(scope:\"{f.Ref}\") first, then {tree}work_threads(scope:\"{f.Ref}\", include:\"followups\") for the follow-ups, and work_get(refs:[\"…\"]) on each item.",
            "Classify each item as do-now, later, drop or needs-a-decision.",
            "Check each against the CURRENT code before you classify it: read the files it names. What is already done or no longer applies is a drop, with the evidence (a file and line, or a commit sha).",
            "Propose an ordered queue of the do-now items in dependency order - what others wait on first - as /rlarch:controller §1 orders a queue.",
            ProposeAndWait,
            "Create no runs and cancel nothing before that yes.");
    }

    private static string Finalize(PromptFacts f)
        => Lines(
            $"Finalize WorkState project {f.Ref} ({f.Title}): no run in it has open work left.",
            $"Call work_brief(scope:\"{f.Ref}\") first, then work_tree(scope:\"{f.Ref}\") and work_threads(scope:\"{f.Ref}\"), and confirm nothing open is left.",
            "- carry any leftover to the backlog: work_status(ref:\"…\", to:\"deferred\", target:\"backlog\", reason:\"…\");",
            "- close each run still open with evidence: run its check yourself, work_verify(ref:\"…\", result_line:\"…\", run_by:\"orchestrator\"), then work_status(ref:\"…\", to:\"done\");",
            $"- then archive the project: work_update(ref:\"{f.Ref}\", archived:true).",
            ProposeAndWait,
            "Ask again before each close and before archiving. Never force a transition.");

    private static string Resume(PromptFacts f)
        => Lines(
            $"Resume WorkState run {f.Ref} ({f.Title}): call work_brief(scope:\"{f.Ref}\") first, then do what its NEXT line says.");

    private static string Audit(PromptFacts f)
        => Lines(
            $"Audit WorkState run {f.Ref} ({f.Title}). {NoWrites}",
            $"Call work_brief(scope:\"{f.Ref}\") first, then work_audit(scope:\"{f.Ref}\").",
            "Compare what the agents reported with what was verified: work_get(refs:[\"…\"]) on a done item shows its reports and its verifications side by side.",
            "List the gaps, each with its ref and the call that would close it: done with no orchestrator verification, a verification whose command is not the item's verify command, a report that claims more than the evidence shows.");

    private static string Work(PromptFacts f)
        => Lines(
            $"Work on WorkState {f.Kind} {f.Ref} ({f.Title}): call work_brief(scope:\"{f.Ref}\", for:\"worker\") first - it holds the criteria, the verify command and the report format.",
            $"Do the work, run the verify command yourself, then hand it back with work_report(ref:\"{f.Ref}\", status:\"done\", files:[…], verify:\"…\").",
            "Never close it yourself unless you hold the orchestrator role.");

    private static string Unblock(PromptFacts f)
        => Lines(
            $"Find out why WorkState {f.Kind} {f.Ref} ({f.Title}) is blocked. {NoWrites}",
            $"Call work_brief(scope:\"{f.Ref}\") first, then work_get(refs:[\"{f.Ref}\"], include:\"deps\") and work_threads(scope:\"{f.Ref}\", include:\"deps\").",
            "Report what it waits on, who or what could unblock it, and the exact call that would - but make none of them.");
}
