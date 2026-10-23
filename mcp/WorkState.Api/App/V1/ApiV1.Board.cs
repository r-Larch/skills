using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Mcp;

namespace WorkState.Api.App.V1;

public static partial class ApiV1
{
    private static readonly HashSet<string> ContainerKinds = ["project", "feature", "run", "phase"];

    /// <summary>
    /// The subtree under any item at or below a run, as one flat pre-order list, the item itself first
    /// at depth 0. By id, so a run <c>backlog</c> and a phase <c>m0/backlog</c> are two boards, never a
    /// 404 over which one a ref meant.
    /// </summary>
    private static async Task<IResult> BoardAsync(
        Guid id,
        BoardQueries board,
        ApiQueries api,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        var book = new V1Book(api, board);
        if (await SnapshotAsync(board, api, workspace, cancellationToken) is not { } snapshot
            || !await KnownAsync(book, snapshot, id, cancellationToken))
        {
            return NoItem(id);
        }

        if (book.RunId(id) is not { } runId)
        {
            return Results.NotFound(new { error = $"item {id} is not a run and sits under none" });
        }

        var rows = await api.BoardAsync(id, cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();
        await book.ItemsAsync(ids, cancellationToken);

        var rollups = (await api.RollupsAsync(ids, cancellationToken)).ToDictionary(r => r.Id);
        var claims = (await book.ClaimsAsync(await api.HeldClaimsAsync([id], cancellationToken), snapshot.AsOf, cancellationToken))
            .ToDictionary(c => c.Item.Id);
        var links = await board.LinksAsync(ids, cancellationToken);
        await book.ItemsAsync(links.Select(l => l.FromId), cancellationToken);

        var blockedBy = links
            .Where(l => l.Type == LinkType.Blocks && !Closed(l.FromStatus))
            .ToLookup(l => l.ToId, l => l.FromId);

        var rootDepth = rows.Count == 0 ? 0 : rows[0].Depth;
        var (branch, worktree) = await api.RunConfigAsync(runId, cancellationToken);

        var views = rows.Select(row =>
        {
            claims.TryGetValue(row.Id, out var claim);

            return new BoardRowView(
                book.Item(row.Id),
                row.ParentId,
                row.Depth - rootDepth,
                V1Text.Seq(row.Seq),
                row.HasChildren,
                V1Text.Resolution(row.Resolution),
                row.Priority,
                row.Severity,
                new CriteriaCountView(row.CriteriaChecked, row.CriteriaTotal),
                ContainerKinds.Contains(row.Kind) || row.HasChildren ? Rollup(rollups.GetValueOrDefault(row.Id)) : null,
                claim,
                row.Commit,
                row is { VerificationPassed: { } passed, VerificationRunBy: { } runBy, VerificationAt: { } at }
                    ? new VerificationSummaryView(passed, V1Text.Snake(runBy), at)
                    : null,
                [.. blockedBy[row.Id].Distinct().Select(book.Item)],
                Flags(row, claim),
                row.OpenedAt,
                row.StartedAt,
                row.ClosedAt);
        }).ToList();

        var root = book.Row(id);

        return Results.Ok(new BoardView(
            snapshot.AsOf,
            snapshot.Seq,
            book.Item(runId),
            book.Item(book.ProjectId(id)),
            root.Intent ?? (runId == id ? null : book.Row(runId).Intent),
            branch,
            worktree,
            Rollup(rollups.GetValueOrDefault(id)),
            views));
    }

    /// <summary>
    /// The board's exceptions, in the vocabulary of doc 06 §2 read the way the text surfaces read it.
    /// <c>disagrees</c> is a closed container over unclosed leaves; <c>done_unverified</c> is the honesty count's.
    /// </summary>
    private static IReadOnlyList<string> Flags(BoardRow row, ClaimView? claim)
    {
        var flags = new List<string>();

        if (row.RollupTotal > 0 && row.RollupDone < row.RollupTotal && Closed(row.Status))
        {
            flags.Add("disagrees");
        }

        // BoardQueries.DoneUnverifiedAsync's predicate exactly — a NULL resolution fails its
        // `<> 'obsolete'` — so a board's flags always sum to the overview's count.
        if (row is { Status: ItemStatus.Done, RequiresVerify: true, OrchestratorVerified: false }
            && row.Resolution is { } resolution && resolution != ItemResolution.Obsolete)
        {
            flags.Add("done_unverified");
        }

        if (row.Forced)
        {
            flags.Add("forced");
        }

        if (claim is { Expired: true })
        {
            flags.Add("stale_claim");
        }

        if (row.UnconsumedNotes)
        {
            flags.Add("unconsumed_notes");
        }

        return flags;
    }

    private static bool Closed(ItemStatus status)
        => status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;
}
