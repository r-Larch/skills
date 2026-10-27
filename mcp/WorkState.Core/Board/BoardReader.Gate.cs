using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;

namespace WorkState.Core.Board;

public sealed partial class BoardReader
{
    /// <summary>
    /// The renderings <c>work_brief(for:)</c> has. doc 01 §9 lists review, audit, handoff and retro
    /// too; they are not built, and a value that silently rendered resume in their place was a
    /// confident wrong answer, so anything outside this list is refused.
    /// </summary>
    public static readonly IReadOnlyList<string> BriefVariants = ["resume", "worker", "gate"];

    public static bool IsGateVariant(string? variant)
        => variant is not null && variant.Trim().Equals("gate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The refusal for a <c>for:</c> this server does not render, or for a gate brief asked of
    /// something that is not a phase; null when the brief can be rendered.
    /// </summary>
    private async Task<string?> RefuseVariantAsync(
        ScopeResult.Found scope,
        string? variant,
        CancellationToken cancellationToken)
    {
        var asked = variant?.Trim();
        var known = string.IsNullOrEmpty(asked)
                    || BriefVariants.Contains(asked, StringComparer.OrdinalIgnoreCase);

        if (known && !(IsGateVariant(asked) && scope.Item.Kind != "phase"))
        {
            return null;
        }

        var callable = await CallableAsync(scope.Item, cancellationToken);

        if (!known)
        {
            return Refusal.Blocked(
                $"work_brief — for:\"{asked}\" is not a rendering this server has.",
                "for: takes \"resume\", \"worker\" or \"gate\" — review, audit, handoff and retro are not built",
                $"work_brief(scope:\"{callable}\") for the session payload",
                $"work_brief(scope:\"{callable}\", for:\"worker\") to hand one item over",
                $"work_brief(scope:\"<phase>\", for:\"gate\") to gate a phase",
                "work_audit(scope) for an audit, work_history(scope) for a retro");
        }

        var run = await board.RunOfAsync(scope.Item.Id, cancellationToken);
        var runRef = run is null ? null : await CallableAsync(run, cancellationToken);
        var routes = new List<string> { $"work_brief(scope:\"{callable}\") for the session payload" };
        routes.Add(runRef is null
            ? $"work_tree(scope:\"{callable}\") to find its phases"
            : $"work_tree(scope:\"{runRef}\") to find the phase, then work_brief(scope:\"<phase>\", for:\"gate\")");

        return Refusal.Blocked(
            $"work_brief — for:\"gate\" gates a phase, and {callable} is a {scope.Item.Kind}.",
            [new Unmet($"{callable} is not a phase", routes)]);
    }

    /// <summary>
    /// doc 01 §9 <c>gate</c>, doc 07 §2: a phase whose tasks are done is ready to be gated, not done.
    /// The seams are computed from <c>item_link</c> on every read — an edge between two of the
    /// phase's tasks, or one with a single end inside it — so a gate never relies on someone having
    /// remembered them. Read-only: nothing is consumed, and the verdict is recorded by the calls the
    /// brief names, which already exist.
    /// </summary>
    private async Task<string> RenderGateBriefAsync(ScopeResult.Found scope, CancellationToken cancellationToken)
    {
        var phase = scope.Item;
        var workspaceId = scope.WorkspaceId;
        var phaseRef = await CallableAsync(phase, cancellationToken);
        var header = await board.HeaderAsync(phase.Id, cancellationToken);
        var subtree = await queries.SubtreeAsync(phase.Id, cancellationToken);
        var tasks = subtree.Where(n => n.Id != phase.Id && n.Kind is "task" or "subtask").ToList();
        var ids = tasks.Select(t => t.Id).ToList();
        var inside = ids.ToHashSet();

        var facts = await FactsAsync(ids, cancellationToken);
        var verifications = (await board.VerificationsAsync(ids, cancellationToken)).ToLookup(v => v.ItemId);
        var links = ids.Count == 0 ? [] : await board.LinksAsync(ids, cancellationToken);
        var done = tasks.Count(t => t.Status == ItemStatus.Done);

        var printed = new Dictionary<Guid, string>();
        async Task<string> Printed(Guid id, string reference)
        {
            if (!printed.TryGetValue(id, out var value))
            {
                value = await board.PrintableRefAsync(workspaceId, id, reference, cancellationToken);
                printed[id] = value;
            }

            return value;
        }

        var run = header?.RunRef is { Length: > 0 } r ? $"  ·  run {r}" : string.Empty;
        var lines = new List<string>
        {
            $"GATE {phaseRef} — {Glyph.Flat(phase.Title)}{run}  ·  {done} of {tasks.Count} tasks done",
        };

        if (header?.Goal is { Length: > 0 } goal)
        {
            lines.AddRange(BriefRenderer.Wrap("GOAL     ", goal, 9));
        }

        if (await board.GateCriterionAsync(phase.Id, cancellationToken) is { } criterion
            && !string.IsNullOrWhiteSpace(criterion))
        {
            lines.AddRange(BriefRenderer.Wrap("PASS IF  ", criterion, 9));
        }

        lines.Add(string.Empty);
        lines.Add($"TASKS ({tasks.Count})");
        foreach (var task in tasks)
        {
            facts.TryGetValue(task.Id, out var fact);
            lines.Add("  " + BoardRenderer.Row(new BoardItem(
                await Printed(task.Id, task.Ref),
                task.Kind,
                task.Status,
                task.Title,
                Evidence: EvidenceOf(fact))));

            var last = verifications[task.Id].MaxBy(v => v.At);
            lines.AddRange(last is null
                ? ["      verify   none recorded"]
                : BriefRenderer.Wrap(
                    "      verify   ",
                    $"{(last.Passed ? "✓" : "✗")} {last.ResultLine} "
                    + $"by {last.RunBy.ToString().ToLowerInvariant()}, {Glyph.Ago(Since(last.At))}",
                    15));
        }

        var seams = links
            .Where(l => l.FromId != l.ToId && (inside.Contains(l.FromId) || inside.Contains(l.ToId)))
            .DistinctBy(l => (l.FromId, l.ToId, l.Type))
            .ToList();

        lines.Add(string.Empty);
        lines.Add(seams.Count == 0
            ? "SEAMS (0) — no work_link edge joins two of these tasks or crosses the phase boundary"
            : $"SEAMS ({seams.Count}) — work_link edges between these tasks or across the phase boundary");
        foreach (var seam in seams)
        {
            var kind = inside.Contains(seam.FromId) && inside.Contains(seam.ToId) ? "inner   " : "crossing";
            lines.Add($"  {kind}  {await Printed(seam.FromId, seam.FromRef)} "
                      + $"{seam.Type.ToString().ToLowerInvariant()} → {await Printed(seam.ToId, seam.ToRef)}");
        }

        lines.Add(string.Empty);
        lines.Add("RECORD THE GATE");
        lines.Add($"  passed   work_verify(ref:\"{phaseRef}\", result_line:\"…\", run_by:\"orchestrator\")  "
                  + $"then  work_status(ref:\"{phaseRef}\", to:\"done\")");
        lines.Add($"  failed   work_finding(findings:[{{target:\"{phaseRef}\", title:\"…\", severity:\"blocker\", "
                  + "source:\"gate\"}]) — a blocker holds the phase open until it is closed");

        var text = await MarkArchivedAsync(phase.Id, string.Join("\n", lines), cancellationToken);
        return Attention.Append(text, await AttentionAsync(scope, cancellationToken));
    }
}
