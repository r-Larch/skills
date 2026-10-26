using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;

namespace WorkState.Core.Board;

/// <summary>
/// Turns the store into the text of <c>docs/workstate/06-rendering.md</c>. It lives in
/// <c>WorkState.Core</c> because the rendering is domain logic: the eight MCP tools are a thin
/// transport over these methods, and the shapes are unit-tested without a web host.
/// </summary>
public sealed partial class BoardReader(WorkStateQueries queries, BoardQueries board)
{
    /// <summary>An <c>in_progress</c> item untouched for this long is a parked agent, not a busy one.</summary>
    private const int StaleHours = 4;

    /// <summary>
    /// doc 06 §4 targets ≤35 lines, paid on every session start. These caps are what keeps it
    /// there on a real run; what they hide is counted in the header and routed to a call.
    /// </summary>
    private const int BriefBindingCap = 5;

    private const int BriefAssumptionCap = 5;
    private const int BriefNoteCap = 3;

    public async Task<ScopeResult> ResolveAsync(
        string? workspaceSlug,
        string? scope,
        CancellationToken cancellationToken = default)
    {
        var workspaceId = await board.WorkspaceAsync(workspaceSlug, cancellationToken);
        if (workspaceId is not { } workspace)
        {
            return new ScopeResult.Empty();
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            var fallback = await board.DefaultScopeAsync(workspace, cancellationToken);
            return fallback is null
                ? new ScopeResult.Empty()
                : new ScopeResult.Found(workspace, fallback);
        }

        var asked = scope.Trim();
        var matches = await board.ResolveAsync(workspace, asked, cancellationToken);

        if (matches.Count == 1)
        {
            return new ScopeResult.Found(workspace, matches[0]);
        }

        if (matches.Count == 0)
        {
            return new ScopeResult.Missing(asked, await NearbyAsync(workspace, asked, cancellationToken));
        }

        var home = await board.DefaultScopeAsync(workspace, cancellationToken);
        return new ScopeResult.Ambiguous(
            asked,
            await board.UniqueRefsAsync(workspace, Prefer(matches, home), cancellationToken),
            matches.Count);
    }

    /// <summary>
    /// <c>work_brief</c> with no scope, for a caller whose repository is known — from its
    /// <c>repo:</c> argument, else from the minted session it was launched as. It opens the
    /// project itself, whose brief names the run to resume. Null when neither names a repository,
    /// which leaves the caller on <see cref="ResolveAsync"/>'s busiest run.
    /// </summary>
    public async Task<ScopeResult?> ResolveRepoAsync(
        string? workspaceSlug,
        IReadOnlyList<RepoKey> asked,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var keys = asked.Count > 0 || sessionId is not { } session
            ? asked
            : (await board.SessionRepoAsync(session, cancellationToken))?.Keys() ?? [];

        if (keys.Count == 0)
        {
            return null;
        }

        var workspaceId = await board.WorkspaceAsync(workspaceSlug, cancellationToken);
        if (workspaceId is not { } workspace)
        {
            return new ScopeResult.Unbound(keys, []);
        }

        var project = await board.ProjectByRepoAsync(workspace, keys, cancellationToken);
        return project is null
            ? new ScopeResult.Unbound(keys, await board.ProjectRefsAsync(workspace, cancellationToken))
            : new ScopeResult.Found(workspace, project);
    }

    /// <summary>
    /// A bare ref that exists in several runs is disambiguated in favour of the caller's own run.
    /// ACCEPTANCE A-1: the refusal named <c>document-store/3.1</c> to an agent working in
    /// <c>entity-store</c> — a callable line that satisfies doc 04 §3 and sends the caller to the
    /// wrong item, which is worse than naming no candidate at all. LINQ ordering is stable, so
    /// everything else keeps the query's plan order.
    /// </summary>
    internal static IReadOnlyList<ScopeRef> Prefer(IReadOnlyList<ScopeRef> matches, ScopeRef? home)
        => home is null ? matches : [.. matches.OrderByDescending(m => AtHome(m, home))];

    private static bool AtHome(ScopeRef candidate, ScopeRef home)
        => candidate.RefScopeId is { } scope && (scope == home.RefScopeId || scope == home.Id);

    /// <summary>
    /// doc 06 §4. The one call that starts a session: goal, position, the literal next action, the
    /// rules that bind, what is unresolved, and what was addressed to whoever picks this up.
    /// </summary>
    public async Task<string> BriefAsync(
        ScopeResult.Found scope,
        string variant,
        CancellationToken cancellationToken = default)
        => (await RenderBriefAsync(scope, variant, cancellationToken)).Text;

    /// <summary>
    /// The brief and the ids of the notes it rendered — what <see cref="BriefSessionAsync"/> may
    /// consume, and nothing else.
    /// </summary>
    private async Task<(string Text, IReadOnlyList<Guid> RenderedNoteIds)> RenderBriefAsync(
        ScopeResult.Found scope,
        string variant,
        CancellationToken cancellationToken)
    {
        var item = scope.Item;
        if (item.Kind == "project")
        {
            return await RenderProjectBriefAsync(scope, variant, cancellationToken);
        }

        var header = await board.HeaderAsync(item.Id, cancellationToken);
        var subtree = await queries.SubtreeAsync(item.Id, cancellationToken);
        var frontier = await queries.ReadyFrontierAsync(item.Id, cancellationToken);
        var constraints = await board.ConstraintsAsync(scope.WorkspaceId, item.Id, cancellationToken);
        var assumptions = await board.AssumptionsAsync(scope.WorkspaceId, item.Id, true, cancellationToken);
        var notes = await board.UnconsumedNotesAsync(scope.WorkspaceId, item.Id, cancellationToken);
        var verify = await board.LastVerificationAsync(item.Id, cancellationToken);
        var attention = await AttentionAsync(scope, cancellationToken);
        var shown = ShownNotes(notes, item.Id, IsWorkerVariant(variant));
        var callable = IsWorkerVariant(variant) ? await CallableAsync(item, cancellationToken) : item.Ref;

        var model = new BriefModel(
            ScopeKind: item.Kind,
            ScopeRef: item.Ref,
            RunRef: header?.RunRef ?? item.Ref,
            ProjectRef: header?.ProjectRef,
            Branch: header?.Branch,
            Head: Short(header?.Head),
            Goal: header?.Goal ?? item.Intent,
            Where: Where(item, subtree),
            Next: IsWorkerVariant(variant)
                ? WorkerNext(callable)
                : Next(item, frontier, subtree, await board.OpenDerivedAsync(item.Id, cancellationToken)),
            Binding: [.. constraints.Take(IsWorkerVariant(variant) ? constraints.Count : BriefBindingCap).Select(ToBinding)],
            BindingTotal: constraints.Count,
            Assumptions: [.. assumptions.Take(BriefAssumptionCap).Select(ToAssumption)],
            AssumptionTotal: assumptions.Count,
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
            Attention: attention);

        var text = BriefRenderer.Render(model, IsWorkerVariant(variant));

        if (await board.ArchivedAtAsync(item.Id, cancellationToken) is { } archivedAt)
        {
            var lines = text.Split('\n');
            lines[0] += $"  ·  archived {archivedAt:yyyy-MM-dd}";
            text = string.Join("\n", lines);
        }

        if (IsWorkerVariant(variant))
        {
            text += "\n\n" + await WorkerSectionAsync(item, callable, cancellationToken);
        }

        return (text, [.. shown.Select(n => n.Id)]);
    }

    /// <summary>
    /// The resume brief caps notes at <see cref="BriefNoteCap"/>. The worker brief renders every note
    /// addressed to its own item, because rendering is what consumes them: a note past the cap would
    /// never be shown, and a pasted brief has no pointer a worker can follow. Notes the item merely
    /// carries keep the cap — they are not consumed, so the pointer to the rest stays true.
    /// </summary>
    private static IReadOnlyList<NoteRow> ShownNotes(IReadOnlyList<NoteRow> notes, Guid itemId, bool worker)
    {
        if (!worker)
        {
            return [.. notes.Take(BriefNoteCap)];
        }

        var carried = 0;
        return [.. notes.Where(n => n.ToItemId == itemId || carried++ < BriefNoteCap)];
    }

    /// <summary>
    /// A brief with the housekeeping a session owes around it: the sweep of doc 07 §5 before, and
    /// the note consumption of doc 01 §3.4 after a worker variant.
    /// <para>
    /// <b>Every surface that serves a brief calls this and nothing else.</b> Both writes mutate the
    /// attention block the brief ends with, so a second surface that rendered the brief without
    /// them — or with its own copy of them — would return different bytes for the same scope
    /// depending only on which surface was called first. The App API and <c>work_brief</c> are one
    /// query layer with two presentations (doc 09 §8), and this method is where that is true.
    /// </para>
    /// </summary>
    public async Task<string> BriefSessionAsync(
        ScopeResult.Found scope,
        string variant,
        Housekeeping housekeeping,
        CancellationToken cancellationToken = default,
        bool signed = false)
    {
        await housekeeping.SweepAsync(scope.WorkspaceId, cancellationToken);

        var (brief, rendered) = await RenderBriefAsync(scope, variant, cancellationToken);

        if (IsWorkerVariant(variant))
        {
            await housekeeping.ConsumeNotesAsync(scope.Item.Id, rendered, cancellationToken);
        }

        return signed ? brief : Unsigned(brief);
    }

    /// <summary>
    /// doc 11 — a caller with no session writes as its role alone, and two of them on one token
    /// cannot be told apart. Said under the header, where every session looks first, and only here:
    /// a write tool answers an unnamed caller without comment.
    /// </summary>
    private static string Unsigned(string brief)
    {
        var lines = brief.Split('\n').ToList();
        lines.Insert(
            Math.Min(1, lines.Count),
            "session  unnamed — register WorkState with header X-WorkState-Agent:<name> to sign your work");
        return string.Join("\n", lines);
    }

    public static bool IsWorkerVariant(string? variant)
        => variant is not null && variant.Equals("worker", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// doc 06 §5, ranked by cost of ignoring. Six sources, five lines: what gets dropped is decided
    /// by <see cref="AttentionRank"/> rather than by which query happened to run last.
    /// </summary>
    public async Task<IReadOnlyList<AttentionLine>> AttentionAsync(
        ScopeResult.Found scope,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<AttentionLine>();
        var item = scope.Item;

        foreach (var claim in await board.ExpiredClaimsAsync(item.Id, cancellationToken))
        {
            var (on, tree) = claim.Tree is { } t
                ? ($" on tree \"{Glyph.Flat(t)}\"", $", tree:\"{t}\"")
                : (string.Empty, string.Empty);

            lines.Add(new AttentionLine(
                AttentionRank.ExpiredClaim,
                $"{claim.ItemRef} claim by {claim.AgentName}{on} expired {Glyph.Age(Since(claim.ExpiresAt))} ago",
                $"work_status(ref:\"{claim.ItemRef}\", to:\"in_progress\"{tree}) to retake it",
                claim.ItemRef,
                claim.QualifiedRef));
        }

        foreach (var blocker in await board.BlockerFindingsAsync(item.Id, cancellationToken))
        {
            var target = blocker.TargetRef is { Length: > 0 } ? $" against {blocker.TargetRef}" : string.Empty;
            lines.Add(new AttentionLine(
                AttentionRank.BlockerFinding,
                $"{blocker.FindingRef} (BLOCKER) is open{target}: {Glyph.Row(blocker.FindingTitle, 40)}",
                $"work_status(ref:\"{blocker.FindingRef}\", to:\"done\")",
                blocker.FindingRef,
                blocker.QualifiedRef));
        }

        // doc 07 §2: nothing promoted this phase and nothing will. The obligation is named, with the
        // call that discharges it, and the agent decides — FINDINGS-live F-L1b.
        foreach (var gate in await board.UngatedPhasesAsync(item.Id, cancellationToken))
        {
            lines.Add(new AttentionLine(
                AttentionRank.PhaseGateNotRun,
                $"{gate.PhaseRef}: all {gate.TaskCount} tasks done, gate not run",
                $"work_brief(scope:\"{gate.PhaseRef}\") to run it",
                gate.PhaseRef,
                gate.QualifiedRef));
        }

        foreach (var unverified in await board.DoneUnverifiedAsync(item.Id, cancellationToken))
        {
            lines.Add(new AttentionLine(
                AttentionRank.DoneUnverified,
                $"{unverified.Ref} is done with no orchestrator verification",
                $"work_verify(ref:\"{unverified.Ref}\", result_line:\"…\", run_by:\"orchestrator\")",
                unverified.Ref,
                unverified.QualifiedRef));
        }

        var assumptions = await board.AssumptionsAsync(scope.WorkspaceId, item.Id, true, cancellationToken);
        foreach (var assumption in assumptions.Where(Due))
        {
            lines.Add(new AttentionLine(
                AttentionRank.AssumptionPastRevisit,
                $"{assumption.Ref} is past revisit_when ({assumption.RevisitRef} is closed)",
                $"work_assume(ref:\"{assumption.Ref}\", resolve:\"confirmed\", evidence:\"…\")",
                assumption.Ref,
                assumption.QualifiedRef));
        }

        // ws/13: one line per addressee. Grouped on the bare ref, two runs' 4.1 merged into a line
        // whose work_brief(scope:"4.1") is refused as ambiguous — and each brief consumes only its
        // own notes anyway. A shared bare ref is printed qualified, as every other call is (ws/10).
        var notes = await board.UnconsumedNotesAsync(
            scope.WorkspaceId, item.Id, cancellationToken, openAddresseesOnly: true);
        var addressees = notes.Where(n => n.ToItemId is not null).GroupBy(n => n.ToItemId!.Value).ToList();
        var shared = (await board.FactsAsync([.. addressees.Select(a => a.Key)], cancellationToken))
            .Where(f => f.RefShared)
            .Select(f => f.Id)
            .ToHashSet();

        foreach (var addressed in addressees)
        {
            var first = addressed.First();
            var named = shared.Contains(addressed.Key)
                ? await AddresseeRefAsync(scope.WorkspaceId, first, cancellationToken)
                : first.ToRef!;

            lines.Add(new AttentionLine(
                AttentionRank.UnconsumedNote,
                $"{addressed.Count()} note(s) addressed to {named} unconsumed",
                $"work_brief(scope:\"{named}\", for:\"worker\")",
                first.ToRef,
                first.ToQualifiedRef));
        }

        foreach (var stale in await board.StaleInProgressAsync(item.Id, StaleHours, cancellationToken))
        {
            lines.Add(new AttentionLine(
                AttentionRank.StaleInProgress,
                $"{stale.Ref} has been in_progress {Glyph.Age(Since(stale.StartedAt))} with no live claim",
                $"work_status(ref:\"{stale.Ref}\", to:\"in_progress\") to retake it",
                stale.Ref,
                stale.QualifiedRef));
        }

        return lines;
    }

    /// <summary>
    /// The ref a worker brief prints for calls and its URI: qualified once another run holds the same
    /// bare ref, since a pasted brief that names an ambiguous ref sends its worker into a refusal.
    /// </summary>
    private async Task<string> CallableAsync(ScopeRef item, CancellationToken cancellationToken)
    {
        var facts = await board.FactsAsync([item.Id], cancellationToken);
        return await CallableAsync(item, facts.FirstOrDefault(), cancellationToken);
    }

    /// <summary>
    /// The scope to print for a note's addressee. A run numbers its own refs, so the note query's
    /// run-qualified ref of a run is its bare ref — ambiguous once anything else holds it. That case
    /// falls back to the form <see cref="BoardQueries.UniqueRefAsync"/> checked to resolve alone.
    /// </summary>
    private async Task<string> AddresseeRefAsync(Guid workspaceId, NoteRow note, CancellationToken cancellationToken)
    {
        var named = note.ToQualifiedRef ?? note.ToRef!;
        var matches = await board.ResolveAsync(workspaceId, named, cancellationToken);
        if (matches.Count <= 1 || matches.FirstOrDefault(m => m.Id == note.ToItemId) is not { } addressee)
        {
            return named;
        }

        return await board.UniqueRefAsync(workspaceId, addressee, cancellationToken) ?? named;
    }

    private async Task<string> WorkerSectionAsync(
        ScopeRef item,
        string callable,
        CancellationToken cancellationToken)
    {
        var criteria = await board.CriteriaAsync([item.Id], cancellationToken);
        var facts = (await board.FactsAsync([item.Id], cancellationToken)).FirstOrDefault();
        var lines = new List<string>(BriefRenderer.Wrap($"YOUR TASK  {item.Ref} — ", item.Title, 11));

        if (item.Intent is { Length: > 0 })
        {
            lines.AddRange(BriefRenderer.Wrap("  intent    ", item.Intent, 12));
        }

        if (item.Status == ItemStatus.InProgress && facts?.HeldBy is { Length: > 0 } holder)
        {
            lines.Add($"  held by   {Glyph.Flat(holder)}");
        }

        lines.Add(criteria.Count == 0
            ? "  criteria  none recorded — work_update(ref:\"" + callable + "\", criteria:[…]) before starting"
            : "  criteria");
        // A done-criterion is what the work is judged against, so it wraps and never clips.
        foreach (var c in criteria)
        {
            var label = $"    {(c.Checked ? "✓" : "✗")} c{c.Seq} ";
            lines.AddRange(BriefRenderer.Wrap(label, c.Text, label.Length));
        }

        // Never clipped: the done guard compares the command recorded by work_verify to this one
        // character for character, and a mismatch used to be invisible until the refusal.
        if (facts?.VerifyCmd is { Length: > 0 } verifyCmd)
        {
            lines.Add($"  verify    {verifyCmd}");
        }

        lines.Add("  report    status / files / verify / red_check / deviations / assumptions / risks / not_done");
        lines.Add($"  brief     {WorkUris.ItemBrief(callable)}");
        lines.Add($"  Next: work_report(ref:\"{callable}\", …) with the fields above; closing is the orchestrator's call.");
        lines.Add($"        work_verify(ref:\"{callable}\", …, run_by:\"worker\") records a claim, not evidence.");

        return string.Join("\n", lines);
    }

    private static bool Due(AssumptionRow assumption)
        => assumption.State == AssumptionState.Open
           && assumption.RevisitStatus is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;

    private static BindingLine ToBinding(ConstraintRow row)
        => new(row.Ref, row.Text, row.Kind == ConstraintKind.RedLine, row.Route);

    private static AssumptionLine ToAssumption(AssumptionRow row)
        => new(
            row.Ref,
            row.Statement,
            row.Evidence.ToString().ToLowerInvariant(),
            row.Reversal?.ToString().ToLowerInvariant(),
            row.RevisitRef);

    private static string Where(ScopeRef item, IReadOnlyList<SubtreeNode> subtree)
    {
        var phases = subtree.Where(n => n.Kind == "phase" && n.ParentId == item.Id).ToList();
        var tasks = subtree.Where(n => n.Id != item.Id && n.Kind is "task" or "subtask").ToList();
        var done = tasks.Count(t => t.Status == ItemStatus.Done);

        if (phases.Count == 0)
        {
            return tasks.Count == 0
                ? $"{Glyph.Kind(item.Kind)} {item.Ref} has no children yet."
                : $"{done} of {tasks.Count} tasks done.";
        }

        var current = phases.FirstOrDefault(p => p.DoingCount > 0 || p.ReviewCount > 0)
                      ?? phases.FirstOrDefault(p => p.OpenCount > 0 || p.BlockedCount > 0)
                      ?? phases[^1];
        var index = phases.IndexOf(current) + 1;

        return $"phase {index} of {phases.Count} — {Glyph.Flat(current.Title)}. "
             + $"{done} of {tasks.Count} tasks done across the run.";
    }

    /// <summary>
    /// The literal next action (doc 06 §4). It names <c>work_get</c> first because that call exists
    /// on every role, and the transition after it, so the line stays a single copyable instruction.
    /// </summary>
    private static string Next(
        ScopeRef item,
        IReadOnlyList<ReadyItem> frontier,
        IReadOnlyList<SubtreeNode> subtree,
        IReadOnlyList<ItemFacts> derived)
    {
        if (frontier.Count > 0)
        {
            var first = frontier[0];
            return $"work_get(refs:[\"{first.Ref}\"])  then  work_status(ref:\"{first.Ref}\", to:\"in_progress\")";
        }

        var doing = subtree.FirstOrDefault(n => n.Status == ItemStatus.InProgress);
        if (doing is not null)
        {
            return $"work_verify(ref:\"{doing.Ref}\", result_line:\"…\", run_by:\"orchestrator\")  "
                 + $"then  work_status(ref:\"{doing.Ref}\", to:\"done\")";
        }

        var blocked = subtree.FirstOrDefault(n => n.Status == ItemStatus.Blocked);
        if (blocked is not null)
        {
            return $"work_threads(scope:\"{item.Ref}\", include:\"deps\") — nothing is ready; "
                 + $"{blocked.Ref} is blocked";
        }

        var open = subtree.Any(n => n.Id != item.Id && n.Status is ItemStatus.Open or ItemStatus.Ready);
        if (open)
        {
            return $"work_next(scope:\"{item.Ref}\")";
        }

        if (item.Status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded)
        {
            var status = item.Status.ToString().ToLowerInvariant();
            return derived is [var first, ..]
                ? $"work_get(refs:[\"{Callable(first.Ref, first)}\"]) — {item.Ref} is {status}; "
                  + $"{Callable(first.Ref, first)} derived from it is still open"
                : $"nothing left — {item.Ref} is {status}";
        }

        return $"work_status(ref:\"{item.Ref}\", to:\"done\") — every child is closed";
    }

    /// <summary>
    /// The worker variant's next action. A worker does not own its task's status — the orchestrator
    /// claims, verifies and closes, and a worker token cannot call <c>work_status</c> — so the line
    /// names the hand-back, never a transition.
    /// </summary>
    private static string WorkerNext(string callable)
        => $"do the task, then work_report(ref:\"{callable}\", status:\"done\", files:[…], verify:\"…\", …)";

    private async Task<IReadOnlyList<string>> NearbyAsync(
        Guid workspaceId,
        string asked,
        CancellationToken cancellationToken)
    {
        var hits = await queries.SearchAsync(workspaceId, asked, 3, cancellationToken);
        return [.. hits.Select(h => h.Ref)];
    }

    private static TimeSpan Since(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }

    private static string? Short(string? sha)
        => sha is { Length: > 8 } ? sha[..8] : sha;
}
