using Microsoft.AspNetCore.Mvc;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Mcp;

namespace WorkState.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>The attention block's stale threshold (<c>BoardReader.StaleHours</c>).</summary>
    private const int StaleHours = 4;

    private static async Task<IResult> InboxAsync(
        [FromQuery] Guid? project,
        BoardQueries board,
        ApiQueries api,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (await SnapshotAsync(board, api, workspace, cancellationToken) is not { } snapshot)
        {
            return Results.Ok(new InboxView(DateTimeOffset.UtcNow, 0, []));
        }

        var book = new V1Book(api, board);
        IReadOnlyList<Guid> scopes;

        if (project is { } id)
        {
            if (!await KnownAsync(book, snapshot, id, cancellationToken))
            {
                return NoItem(id);
            }

            scopes = [id];
        }
        else
        {
            scopes = [.. (await board.ProjectsAsync(snapshot.WorkspaceId, cancellationToken)).Select(p => p.Id)];
        }

        var rows = await InboxRowsAsync(snapshot, scopes, book, board, api, cancellationToken);
        return Results.Ok(new InboxView(snapshot.AsOf, snapshot.Seq, rows));
    }

    private sealed record Waiting(string Kind, Guid ItemId, string What, DateTimeOffset Since, Func<string, string> Call);

    /// <summary>
    /// Everything waiting on a human under the scopes, newest first. Each source is the query the
    /// attention block uses for the same line (<c>BoardReader.AttentionAsync</c>), so the inbox and
    /// what an agent is told never disagree. One row per kind and item, so the key is stable.
    /// </summary>
    private static async Task<IReadOnlyList<InboxRowView>> InboxRowsAsync(
        Snapshot snapshot,
        IReadOnlyList<Guid> scopes,
        V1Book book,
        BoardQueries board,
        ApiQueries api,
        CancellationToken cancellationToken)
    {
        var waiting = new List<Waiting>();

        foreach (var scope in scopes)
        {
            foreach (var row in await api.BlockedOnAPersonAsync(scope, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "blocked", row.Id, "blocked, and nothing open blocks it — it waits on a decision",
                    row.Since, q => $"work_status(ref:\"{q}\", to:\"in_progress\")"));
            }

            foreach (var blocker in await board.BlockerFindingsAsync(scope, cancellationToken))
            {
                var against = blocker.TargetRef is { Length: > 0 } target ? $" against {target}" : string.Empty;
                waiting.Add(new Waiting(
                    "blocker_finding", blocker.FindingId, $"BLOCKER finding open{against}: {blocker.FindingTitle}",
                    DateTimeOffset.MinValue, q => $"work_status(ref:\"{q}\", to:\"done\")"));
            }

            foreach (var question in await api.OpenQuestionsAsync(scope, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "question", question.Id, "open question",
                    question.Since, q => $"work_status(ref:\"{q}\", to:\"done\")"));
            }

            foreach (var claim in await board.ExpiredClaimsAsync(scope, cancellationToken))
            {
                var (on, tree) = claim.Tree is { } t ? ($" on tree \"{t}\"", $", tree:\"{t}\"") : (string.Empty, string.Empty);
                waiting.Add(new Waiting(
                    "expired_claim", claim.ItemId, $"claim by {claim.AgentName}{on} expired",
                    claim.ExpiresAt, q => $"work_status(ref:\"{q}\", to:\"in_progress\"{tree})"));
            }

            var due = (await board.AssumptionsAsync(snapshot.WorkspaceId, scope, true, cancellationToken))
                .Where(a => a.ScopeItemId is not null
                            && a.State == AssumptionState.Open
                            && a.RevisitStatus is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded)
                .GroupBy(a => a.ScopeItemId!.Value);

            foreach (var group in due)
            {
                var first = group.First();
                waiting.Add(new Waiting(
                    "assumption_due", group.Key,
                    $"{group.Count()} assumption(s) past revisit_when: {string.Join(", ", group.Select(a => a.Ref))}",
                    group.Max(a => a.RevisitClosedAt) ?? DateTimeOffset.MinValue,
                    _ => $"work_assume(ref:\"{first.QualifiedRef ?? first.Ref}\", resolve:\"confirmed\", evidence:\"…\")"));
            }

            foreach (var unverified in await board.DoneUnverifiedAsync(scope, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "done_unverified", unverified.Id, "done with no orchestrator verification",
                    unverified.ClosedAt ?? DateTimeOffset.MinValue,
                    q => $"work_verify(ref:\"{q}\", result_line:\"…\", run_by:\"orchestrator\")"));
            }

            foreach (var stale in await board.StaleInProgressAsync(scope, StaleHours, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "stale_in_progress", stale.Id, "in_progress with no live claim",
                    stale.StartedAt, q => $"work_status(ref:\"{q}\", to:\"in_progress\")"));
            }

            var notes = await board.UnconsumedNotesAsync(
                snapshot.WorkspaceId, scope, cancellationToken, openAddresseesOnly: true);

            foreach (var addressed in notes.Where(n => n.ToItemId is not null).GroupBy(n => n.ToItemId!.Value))
            {
                waiting.Add(new Waiting(
                    "unconsumed_note", addressed.Key, $"{addressed.Count()} note(s) addressed to it unconsumed",
                    addressed.Min(n => n.CreatedAt), q => $"work_brief(scope:\"{q}\", for:\"worker\")"));
            }
        }

        await book.PlacedAsync(waiting.Select(w => w.ItemId), cancellationToken);

        return [.. waiting
            .DistinctBy(w => (w.Kind, w.ItemId))
            .Select(w =>
            {
                var item = book.Item(w.ItemId);
                var since = w.Since == DateTimeOffset.MinValue ? book.Row(w.ItemId).OpenedAt : w.Since;
                return new InboxRowView(
                    $"{w.Kind}:{w.ItemId}",
                    w.Kind,
                    item,
                    book.RunOf(w.ItemId),
                    w.What,
                    since,
                    w.Call(item.QualifiedRef));
            })
            .OrderByDescending(r => r.Since)
            .ThenBy(r => r.Key, StringComparer.Ordinal)];
    }
}
