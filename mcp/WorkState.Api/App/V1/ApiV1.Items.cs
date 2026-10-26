using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using WorkState.Core.Board;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Mcp;

namespace WorkState.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>
    /// A unit page: what was asked, what the agent <em>reported</em> (<c>work_report</c>), and what was
    /// <em>verified</em> (<c>work_verify</c>). Kept apart because a report is a claim and a
    /// verification is evidence, and the done guard only ever counts the second.
    /// </summary>
    private static async Task<IResult> ItemAsync(
        Guid id,
        BoardQueries board,
        ApiQueries api,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        var book = new V1Book(api, board);
        if (await SnapshotAsync(board, api, workspace, cancellationToken) is not { } snapshot
            || !await KnownAsync(book, snapshot, id, cancellationToken)
            || await api.ItemAsync(id, cancellationToken) is not { } item)
        {
            return NoItem(id);
        }

        var path = await api.AncestorsAsync(id, cancellationToken);
        var criteria = await api.CriteriaAsync(id, cancellationToken);
        var reports = await api.ReportsAsync(id, cancellationToken);
        var verifications = await api.VerificationsAsync(id, cancellationToken);
        var artifacts = await api.ArtifactsAsync(id, cancellationToken);
        var children = await api.ChildrenAsync(id, cancellationToken);
        var findings = await api.FindingsAsync(id, cancellationToken);
        var notes = await api.NotesAsync(id, cancellationToken);
        var links = await board.LinksAsync([id], cancellationToken);
        var assumptions = await api.AssumptionsAsync(id, cancellationToken);
        var claim = (await api.HeldClaimsAsync([id], cancellationToken)).FirstOrDefault(c => c.ItemId == id);

        var page = await api.EventsAsync(
            snapshot.WorkspaceId, id, exact: true, 0, ItemEventLimit + 1, oldestFirst: false, null, cancellationToken);

        await book.ItemsAsync(
            path
                .Concat(children.Select(c => c.Id))
                .Concat(findings.Select(f => f.Id))
                .Concat(links.SelectMany(l => new[] { l.FromId, l.ToId })),
            cancellationToken);
        await book.ItemsAsync(
            notes.SelectMany(n => new[] { n.FromItemId, n.ToItemId })
                .Concat(assumptions.Select(a => a.RevisitItemId)),
            cancellationToken);
        await book.SessionsAsync(
            reports.Select(r => r.SessionId)
                .Concat(verifications.Select(v => v.SessionId))
                .Concat(notes.Select(n => n.SessionId)),
            cancellationToken);

        var claimView = claim is null ? null : (await book.ClaimsAsync([claim], snapshot.AsOf, cancellationToken))[0];
        var events = await book.EventsAsync([.. page.Take(ItemEventLimit).Reverse()], cancellationToken);

        return Results.Ok(new ItemPageView(
            snapshot.AsOf,
            snapshot.Seq,
            book.Item(id),
            [.. path.Select(book.Item)],
            item.Intent,
            item.VerifyCmd,
            Strings(item.RiskTags),
            item.Priority,
            item.Severity,
            item.Source,
            V1Text.Resolution(item.Resolution),
            item.ClosedBy,
            item.OpenedAt,
            item.StartedAt,
            item.ClosedAt,
            [.. criteria.Select(c => new CriterionView($"c{c.Seq}", c.Text, c.Checked, c.Evidence, c.CheckedAt))],
            claimView,
            [.. reports.Select(r => new ReportView(
                r.Attempt,
                book.Session(r.SessionId),
                r.AgentName,
                r.StartedAt,
                r.EndedAt,
                r.Status,
                Strings(r.Files),
                Section(r.Raw, "VERIFY", "RED-CHECK"),
                Section(r.Raw, "RED-CHECK", "DEVIATIONS"),
                r.Deviations,
                r.Risks,
                r.NotDone,
                r.At))],
            [.. verifications.Select(v => new VerificationView(
                v.Command,
                v.ResultLine,
                v.Passed,
                V1Book.Json(v.Counts),
                V1Text.Snake(v.RunBy),
                book.Session(v.SessionId),
                RedCheck(v.RedCheck),
                v.At))],
            [.. artifacts.Select(a => new ArtifactView(a.Kind, a.Value, a.At))],
            [.. children.Select(c => new ChildView(book.Item(c.Id), new CriteriaCountView(c.CriteriaChecked, c.CriteriaTotal)))],
            [.. findings.Select(f => new FindingView(book.Item(f.Id), f.Severity, f.Source))],
            [.. notes.Select(n => new NoteView(
                n.Id,
                V1Text.Snake(n.Kind),
                n.Body,
                book.Item(n.FromItemId),
                book.Item(n.ToItemId),
                n.ConsumedAt,
                n.CreatedAt,
                book.Session(n.SessionId)))],
            new LinksView(
                [.. links.Where(l => l.FromId == id).Select(l => new LinkView(V1Text.Snake(l.Type), book.Item(l.ToId)))],
                [.. links.Where(l => l.ToId == id).Select(l => new LinkView(V1Text.Snake(l.Type), book.Item(l.FromId)))]),
            [.. assumptions.Select(a => new AssumptionView(
                a.Ref, a.Statement, V1Text.Snake(a.State), book.Item(a.RevisitItemId)))],
            events,
            page.Count > ItemEventLimit));
    }

    /// <summary>
    /// The verbatim <c>work_brief</c>, through the one method every brief surface calls — so its
    /// housekeeping runs here too, exactly as in the tool.
    /// </summary>
    private static async Task<IResult> BriefAsync(
        Guid id,
        [FromQuery(Name = "for")] string? variant,
        BoardReader reader,
        BoardQueries board,
        ApiQueries api,
        Housekeeping housekeeping,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        var book = new V1Book(api, board);
        if (await SnapshotAsync(board, api, workspace, cancellationToken) is not { } snapshot
            || !await KnownAsync(book, snapshot, id, cancellationToken))
        {
            return NoItem(id);
        }

        var asked = string.IsNullOrWhiteSpace(variant) ? "resume" : variant.Trim().ToLowerInvariant();
        if (asked is not ("resume" or "worker"))
        {
            return Results.BadRequest(new { error = "for= is resume or worker" });
        }

        var brief = await reader.BriefSessionAsync(
            new ScopeResult.Found(snapshot.WorkspaceId, book.Row(id).Scope), asked, housekeeping, cancellationToken);

        return Results.Text(brief, "text/plain");
    }

    private static IReadOnlyList<string> Strings(string? json)
        => V1Book.Json(json) is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];

    /// <summary>
    /// <c>verify</c> and <c>red_check</c> have no column of their own: <c>work_report</c> keeps them in
    /// the raw report it writes (<c>WorkWriter.Raw</c>), one labelled line each. Absent there, absent here.
    /// </summary>
    private static string? Section(string? raw, string label, string next)
    {
        if (raw is null)
        {
            return null;
        }

        var marker = $"{label}: ";
        var at = raw.StartsWith(marker, StringComparison.Ordinal)
            ? 0
            : raw.IndexOf($"\n{marker}", StringComparison.Ordinal) is var line and >= 0 ? line + 1 : -1;
        if (at < 0)
        {
            return null;
        }

        var start = at + marker.Length;
        var end = raw.IndexOf($"\n{next}: ", start, StringComparison.Ordinal);
        var value = (end < 0 ? raw[start..] : raw[start..end]).Trim();

        return value.Length == 0 ? null : value;
    }

    /// <summary><c>work_verify</c> stores its red check as <c>{"note": …}</c>; anything else is shown as stored.</summary>
    private static string? RedCheck(string? stored)
        => V1Book.Json(stored) switch
        {
            null => null,
            { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("note", out var note)
                                                       && note.ValueKind == JsonValueKind.String => note.GetString(),
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            var other => other.Value.GetRawText(),
        };
}
