using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;

namespace WorkState.Core.Board;

public sealed partial class BoardReader
{
    private const int RecentRunCap = 5;
    private const int BacklogCap = 5;

    /// <summary>
    /// doc 06 §4a — the brief of a project, the continue-here context a fresh session in its repo
    /// opens: every open run with its next action, the runs closed most recently, the waiting
    /// backlog, and a NEXT that resumes a run or starts one.
    /// </summary>
    private async Task<(string Text, IReadOnlyList<Guid> RenderedNoteIds)> RenderProjectBriefAsync(
        ScopeResult.Found scope,
        string variant,
        CancellationToken cancellationToken)
    {
        var project = scope.Item;
        var worker = IsWorkerVariant(variant);
        var runs = await board.ProjectRunsAsync(project.Id, WorkWriter.BacklogRunTitle, cancellationToken);
        var facts = await FactsAsync(runs.Select(r => r.Id).Append(project.Id), cancellationToken);
        var projectRef = await CallableAsync(project, facts.GetValueOrDefault(project.Id), cancellationToken);

        var open = runs.Where(r => !r.Closed && !r.Backlog).ToList();
        var closed = runs.Where(r => r.Closed).ToList();
        var backlogRun = runs.FirstOrDefault(r => !r.Closed && r.Backlog);

        var openLines = new List<OpenRunLine>();
        foreach (var run in open)
        {
            openLines.Add(new OpenRunLine(
                await CallableAsync(run.Scope, facts.GetValueOrDefault(run.Id), cancellationToken),
                run.Title,
                run.TasksDone,
                run.TasksTotal,
                await RunNextAsync(run, cancellationToken),
                [.. (await board.LiveClaimsAsync(run.Id, cancellationToken))
                    .Where(c => c.ExpiresAt >= DateTimeOffset.UtcNow)
                    .Select(c => $"{c.ItemRef} by {Glyph.Flat(c.AgentName)}")]));
        }

        var recent = new List<ClosedRunLine>();
        foreach (var run in closed.Take(RecentRunCap))
        {
            recent.Add(new ClosedRunLine(
                await CallableAsync(run.Scope, facts.GetValueOrDefault(run.Id), cancellationToken),
                run.Title,
                ProjectBriefRenderer.Outcome(run.Status, run.Resolution),
                run.ClosedAt,
                Short(run.Commit)));
        }

        var backlog = await board.ProjectBacklogAsync(project.Id, backlogRun?.Id, cancellationToken);
        var constraints = await board.ConstraintsAsync(scope.WorkspaceId, project.Id, cancellationToken);
        var assumptions = await board.AssumptionsAsync(scope.WorkspaceId, project.Id, true, cancellationToken);
        var notes = await board.UnconsumedNotesAsync(scope.WorkspaceId, project.Id, cancellationToken);
        var shown = ShownNotes(notes, project.Id, worker);
        var verify = await board.LastVerificationAsync(project.Id, cancellationToken);
        var repos = await board.ProjectReposAsync([project.Id], cancellationToken);

        var model = new ProjectBriefModel(
            ProjectRef: projectRef,
            Title: project.Title,
            Repos: repos.Count == 0 ? null : string.Join(", ", repos.Select(r => $"{r.Kind} {r.Key}")),
            Goal: project.Intent,
            OpenRuns: openLines,
            Recent: recent,
            ClosedTotal: closed.Count,
            ClosedRoute: $"work_tree(scope:\"{projectRef}\", depth:1, status:\"done,cancelled,superseded\")",
            Backlog: [.. backlog.Take(BacklogCap).Select(b => new BacklogLine(b.RefShared ? b.QualifiedRef : b.Ref, b.Kind, b.Title))],
            BacklogTotal: backlog.Count,
            BacklogRoute: await BacklogRouteAsync(projectRef, backlogRun, facts, backlog.Count, cancellationToken),
            Binding: [.. constraints.Take(worker ? constraints.Count : BriefBindingCap).Select(ToBinding)],
            BindingTotal: constraints.Count,
            Notes: [.. shown.Select(n => Glyph.Flat(n.Body))],
            NoteTotal: notes.Count,
            AddressedTotal: notes.Count(n => n.ToRef is { Length: > 0 }),
            AddressedTo: notes.FirstOrDefault(n => n.ToRef is { Length: > 0 })?.ToRef,
            LastVerify: verify is null
                ? null
                : new VerifyLine(
                    verify.Command,
                    verify.ResultLine,
                    verify.RunBy.ToString().ToLowerInvariant(),
                    Since(verify.At),
                    verify.Passed),
            Next: open.Count > 0
                ? $"resume run {openLines[0].Ref} — work_brief(scope:\"{openLines[0].Ref}\")"
                : "start a run under this project — "
                  + $"work_create(items:[{{kind:\"run\", parent:\"{projectRef}\", ref:\"<slug>\", title:\"…\"}}])",
            Attention: await AttentionAsync(scope, cancellationToken),
            // Several runs number their own A1, so a project prints each assumption's qualified ref.
            Assumptions: [.. assumptions.Take(BriefAssumptionCap)
                .Select(a => ToAssumption(a) with { Ref = a.QualifiedRef ?? a.Ref })],
            AssumptionTotal: assumptions.Count);

        return (ProjectBriefRenderer.Render(model), [.. shown.Select(n => n.Id)]);
    }

    /// <summary>
    /// What <c>work_next</c> would say for the run, or why nothing is ready. Never a transition of the
    /// run itself — the project brief names work inside it.
    /// </summary>
    private async Task<string> RunNextAsync(ProjectRunRow run, CancellationToken cancellationToken)
    {
        var frontier = await queries.ReadyFrontierAsync(run.Id, cancellationToken);
        if (frontier.Count > 0)
        {
            var first = frontier[0];
            var facts = await FactsAsync([first.Id], cancellationToken);
            var callable = Callable(first.Ref, facts.GetValueOrDefault(first.Id));
            return $"work_get(refs:[\"{callable}\"]) then work_status(ref:\"{callable}\", to:\"in_progress\")";
        }

        var subtree = await queries.SubtreeAsync(run.Id, cancellationToken);
        var deps = $"work_threads(scope:\"{run.Ref}\", include:\"deps\")";

        if (subtree.FirstOrDefault(n => n.Status == ItemStatus.InProgress) is { } doing)
        {
            return $"nothing ready — {doing.Ref} is in progress; {deps}";
        }

        if (subtree.FirstOrDefault(n => n.Status == ItemStatus.Blocked) is { } blocked)
        {
            return $"nothing ready — {blocked.Ref} is blocked; {deps}";
        }

        if (subtree.FirstOrDefault(n => n.Status == ItemStatus.Review) is { } review)
        {
            return $"nothing ready — {review.Ref} is in review";
        }

        return subtree.Any(n => n.Id != run.Id && n.Status is ItemStatus.Open or ItemStatus.Ready)
            ? $"nothing ready — open work waits on its dependencies; {deps}"
            : $"nothing ready — the run has no open work; work_brief(scope:\"{run.Ref}\") to close or plan it";
    }

    private async Task<string> BacklogRouteAsync(
        string projectRef,
        ProjectRunRow? backlogRun,
        IReadOnlyDictionary<Guid, ItemFacts> facts,
        int count,
        CancellationToken cancellationToken)
    {
        var followups = $"work_threads(scope:\"{projectRef}\", include:\"followups\")";
        if (backlogRun is null || count == 0)
        {
            return followups;
        }

        var run = await CallableAsync(backlogRun.Scope, facts.GetValueOrDefault(backlogRun.Id), cancellationToken);
        return $"work_tree(scope:\"{run}\") and {followups}";
    }
}
