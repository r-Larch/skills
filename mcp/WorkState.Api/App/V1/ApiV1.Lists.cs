using Microsoft.AspNetCore.Mvc;
using WorkState.Core.Queries;
using WorkState.Core.Writing;
using WorkState.Mcp;

namespace WorkState.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>
    /// doc 10's <c>kinds</c> vocabulary over the journal's own event kinds. An event keeps its stored
    /// kind in the response; the filter is the only place these names appear.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> KindFilters = new Dictionary<string, string[]>
    {
        ["transition"] = ["status", "forced_transition", "deferred"],
        ["verification"] = ["verified"],
        ["report"] = ["reported"],
        ["finding"] = ["finding"],
        ["note"] = ["note"],
        ["claim"] = ["claim_acquired", "claim_expired"],
        ["link"] = ["linked", "unlinked"],
        ["created"] = ["created"],
        ["carried"] = ["carried"],
    };

    /// <summary>
    /// Ascending by seq. With no <c>since</c> it is the newest page, reversed into the same order;
    /// with one it is the contiguous page after the cursor. With <c>before</c> it is the newest page
    /// below that seq, reversed the same way, and its cursor is the next <c>before</c>.
    /// </summary>
    private static async Task<IResult> JournalAsync(
        [FromQuery] Guid? scope,
        [FromQuery] long? since,
        [FromQuery] long? before,
        [FromQuery] int? limit,
        [FromQuery] string? kinds,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (since is not null && before is not null)
        {
            return Results.BadRequest(new
            {
                error = "since= and before= cannot be combined: since pages forward from a cursor, before pages back from one",
            });
        }

        string[]? wanted = null;
        if (!string.IsNullOrWhiteSpace(kinds))
        {
            var asked = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = asked.Where(k => !KindFilters.ContainsKey(k)).ToList();
            if (unknown.Count > 0)
            {
                return Results.BadRequest(new
                {
                    error = $"unknown kinds: {string.Join(",", unknown)}; known: {string.Join(",", KindFilters.Keys)}",
                });
            }

            wanted = [.. asked.SelectMany(k => KindFilters[k]).Distinct()];
        }

        var cursor = Math.Max(since ?? 0, 0);

        var book = new V1Book(api, board);
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        if (scope is { } id && !await KnownAsync(book, snapshot, id, cancellationToken))
        {
            return NoItem(id);
        }

        var take = Math.Clamp(limit ?? JournalDefaultLimit, 1, JournalMaxLimit);

        if (before is { } below)
        {
            var older = await api.EventsAsync(
                snapshot.WorkspaceId, scope, exact: false, 0, take + 1, oldestFirst: false, wanted, cancellationToken, below);
            IReadOnlyList<EventDetail> back = [.. older.Take(take).Reverse()];

            return Results.Ok(new JournalPageView(
                snapshot.AsOf,
                snapshot.Seq,
                back.Count == 0 ? below : back[0].Seq,
                older.Count > take,
                await book.EventsAsync(back, cancellationToken)));
        }

        var page = await api.EventsAsync(
            snapshot.WorkspaceId, scope, exact: false, cursor, take + 1, oldestFirst: cursor > 0, wanted, cancellationToken);

        var more = page.Count > take;
        IReadOnlyList<EventDetail> rows = cursor > 0 ? [.. page.Take(take)] : [.. page.Take(take).Reverse()];

        return Results.Ok(new JournalPageView(
            snapshot.AsOf,
            snapshot.Seq,
            rows.Count == 0 ? cursor : rows[^1].Seq,
            more,
            await book.EventsAsync(rows, cancellationToken)));
    }

    /// <summary>
    /// The project's backlog run — where <c>target:"backlog"</c> deferrals land — and its open work,
    /// grouped by the run each item was deferred out of (<c>derived_from</c>).
    /// </summary>
    private static async Task<IResult> BacklogAsync(
        [FromQuery] Guid? project,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (project is not { } projectId)
        {
            return Results.BadRequest(new { error = "project= is required: the backlog run is per project" });
        }

        var book = new V1Book(api, board);
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);
        if (!await KnownAsync(book, snapshot, projectId, cancellationToken))
        {
            return NoItem(projectId);
        }

        if (await api.BacklogRunAsync(projectId, WorkWriter.BacklogRunTitle, cancellationToken) is not { } runId)
        {
            return Results.Ok(new BacklogView(snapshot.AsOf, snapshot.Seq, null, []));
        }

        var rows = await api.BacklogAsync(runId, cancellationToken);
        await book.ItemsAsync(rows.Select(r => r.Id).Append(runId), cancellationToken);
        await book.PlacedAsync(rows.Select(r => r.OriginId), cancellationToken);

        var groups = rows
            .GroupBy(r => r.OriginId is { } origin ? book.RunId(origin) : null)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key is { } run ? book.Item(run).QualifiedRef : string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BacklogGroupView(book.Item(g.Key), [.. g.Select(r => book.Item(r.Id))]))
            .ToList();

        return Results.Ok(new BacklogView(snapshot.AsOf, snapshot.Seq, book.Item(runId), groups));
    }

    /// <summary>doc 11's sessions, most recently seen first, each with what it holds now.</summary>
    private static async Task<IResult> SessionsAsync(
        [FromQuery] bool? active,
        [FromQuery] string? name,
        [FromQuery] string? claudeSessionId,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        var book = new V1Book(api, board);
        var sessions = await api.SessionsAsync(
            snapshot.WorkspaceId,
            active == true,
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            string.IsNullOrWhiteSpace(claudeSessionId) ? null : claudeSessionId.Trim(),
            cancellationToken);

        var claims = await api.SessionClaimsAsync([.. sessions.Select(s => s.Id)], cancellationToken);
        var views = await book.ClaimsAsync(claims, snapshot.AsOf, cancellationToken);
        var held = claims.Zip(views).ToLookup(pair => pair.First.SessionId, pair => pair.Second);
        var tokens = (await api.SessionTokensAsync([.. sessions.Select(s => s.Id)], cancellationToken))
            .ToDictionary(t => t.Id, t => t.Token);

        return Results.Ok(new SessionsView(
            snapshot.AsOf,
            snapshot.Seq,
            [.. sessions.Select(s => new SessionHoldingView(V1Book.Session(s), [.. held[s.Id]], tokens.GetValueOrDefault(s.Id, "none")))]));
    }

    /// <summary>Rule 1's one door from a ref to an id: zero, one or many matches, and the client decides.</summary>
    private static async Task<IResult> ResolveAsync(
        [FromQuery(Name = "ref")] string? reference,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Results.BadRequest(new { error = "ref= is required" });
        }

        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        var book = new V1Book(api, board);
        var matches = await board.ResolveAsync(snapshot.WorkspaceId, reference.Trim(), cancellationToken);
        await book.ItemsAsync(matches.Select(m => m.Id), cancellationToken);

        return Results.Ok(new ResolveView(snapshot.AsOf, snapshot.Seq, [.. matches.Select(m => book.Item(m.Id))]));
    }

    /// <summary>
    /// The dashboard's find: exact refs by <see cref="ResolveAsync"/>'s rule first, then refs that
    /// start with the query, then full-text item hits — each item once, at its best match.
    /// </summary>
    private static async Task<IResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] int? limit,
        BoardQueries board,
        ApiQueries api,
        WorkStateQueries queries,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest(new { error = "q= is required" });
        }

        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        var query = q.Trim();
        var take = Math.Clamp(limit ?? SearchDefaultLimit, 1, SearchMaxLimit);
        var hits = new List<(Guid Id, string Match, string? Snippet)>();
        var seen = new HashSet<Guid>();

        void Add(Guid id, string match, string? snippet)
        {
            if (hits.Count < take && seen.Add(id))
            {
                hits.Add((id, match, snippet));
            }
        }

        foreach (var match in await board.ResolveAsync(snapshot.WorkspaceId, query, cancellationToken))
        {
            Add(match.Id, "ref", null);
        }

        if (hits.Count < take)
        {
            foreach (var id in await api.RefPrefixAsync(snapshot.WorkspaceId, query, take, cancellationToken))
            {
                Add(id, "prefix", null);
            }
        }

        if (hits.Count < take)
        {
            foreach (var hit in await queries.SearchAsync(snapshot.WorkspaceId, query, take, null, itemsOnly: true, cancellationToken))
            {
                Add(hit.Id, "text", hit.Snippet.Replace("<b>", string.Empty).Replace("</b>", string.Empty));
            }
        }

        var book = new V1Book(api, board);
        await book.PlacedAsync(hits.Select(h => h.Id), cancellationToken);

        return Results.Ok(new SearchView(
            snapshot.AsOf,
            snapshot.Seq,
            [.. hits.Select(h => new SearchHitView(
                book.Item(h.Id), book.RunOf(h.Id), book.Item(book.ProjectId(h.Id)), h.Match, h.Snippet))]));
    }
}
