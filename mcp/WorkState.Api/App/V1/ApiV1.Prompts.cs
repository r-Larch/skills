using Microsoft.AspNetCore.Mvc;
using WorkState.Core.Board;
using WorkState.Core.Queries;
using WorkState.Mcp;

namespace WorkState.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>
    /// The prompt actions that apply to one scope, built by <see cref="BoardReader.PromptsAsync"/> so
    /// the dashboard and the skills copy one wording. Read-only: nothing here sweeps or consumes.
    /// </summary>
    private static async Task<IResult> PromptsAsync(
        [FromQuery] Guid? scope,
        BoardReader reader,
        BoardQueries board,
        ApiQueries api,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (scope is not { } id)
        {
            return Results.BadRequest(new { error = "scope= is required: the id of a project, run or item" });
        }

        var book = new V1Book(api, board);
        if (await SnapshotAsync(board, api, workspace, cancellationToken) is not { } snapshot
            || !await KnownAsync(book, snapshot, id, cancellationToken))
        {
            return NoItem(id);
        }

        var prompts = await reader.PromptsAsync(
            new ScopeResult.Found(snapshot.WorkspaceId, book.Row(id).Scope), cancellationToken);

        return Results.Ok(new PromptsView(
            snapshot.AsOf,
            snapshot.Seq,
            book.Item(id),
            [.. prompts.Select(p => new PromptView(p.Key, p.Label, p.Count, p.Text))]));
    }
}
