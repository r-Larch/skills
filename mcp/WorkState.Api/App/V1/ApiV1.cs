using System.Security.Claims;
using WorkState.Core.Queries;
using WorkState.Mcp;

namespace WorkState.Api.App.V1;

/// <summary>
/// doc 10 — the JSON read API, one contract for the dashboard and Foreman. Read-only: the MCP tools
/// stay the only write path. URLs take ids; a ref becomes an id through <c>/resolve</c> alone.
/// </summary>
public static partial class ApiV1
{
    /// <summary>Cookie, any agent token, or a reader token (rule 5).</summary>
    public const string Policy = "api-v1-read";

    public const string Header = "X-WorkState-Api";

    public const string Version = "1";

    private const int JournalDefaultLimit = 50;
    private const int JournalMaxLimit = 500;
    private const int ItemEventLimit = 50;

    public static bool IsReader(ClaimsPrincipal user) => user.IsInRole(WorkStatePolicies.Reader);

    /// <summary>
    /// Ahead of authentication, so a 401 carries the header too: a client must be able to tell a v1
    /// refusal from some other server's.
    /// </summary>
    public static IApplicationBuilder UseApiVersionHeader(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1"))
            {
                context.Response.Headers[Header] = Version;
            }

            await next();
        });

    public static void MapApiV1(this RouteGroupBuilder api)
    {
        var v1 = api.MapGroup("/v1").RequireAuthorization(Policy);

        v1.MapGet("/overview", OverviewAsync);
        v1.MapGet("/runs/{id:guid}/board", BoardAsync);
        v1.MapGet("/items/{id:guid}", ItemAsync);
        v1.MapGet("/items/{id:guid}/brief", BriefAsync);
        v1.MapGet("/inbox", InboxAsync);
        v1.MapGet("/journal", JournalAsync);
        v1.MapGet("/backlog", BacklogAsync);
        v1.MapGet("/sessions", SessionsAsync);
        v1.MapGet("/resolve", ResolveAsync);
    }

    /// <summary>
    /// What every body opens with (rule 3). The seq is read before anything else, so whatever the
    /// response goes on to show reflects at least that much of the journal.
    /// </summary>
    private sealed record Snapshot(Guid WorkspaceId, DateTimeOffset AsOf, long Seq);

    private static async Task<Snapshot?> SnapshotAsync(
        BoardQueries board,
        ApiQueries api,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        var asOf = DateTimeOffset.UtcNow;

        return await board.WorkspaceAsync(workspace.Slug, cancellationToken) is { } id
            ? new Snapshot(id, asOf, await api.SeqAsync(id, cancellationToken))
            : null;
    }

    /// <summary>The item, if it exists in this workspace — an id from another one is as unknown as a typo.</summary>
    private static async Task<bool> KnownAsync(V1Book book, Snapshot snapshot, Guid id, CancellationToken cancellationToken)
    {
        await book.PlacedAsync([id], cancellationToken);
        return book.Has(id) && book.Row(id).WorkspaceId == snapshot.WorkspaceId;
    }

    private static IResult NoItem(Guid id) => Results.NotFound(new { error = $"no item with id {id}" });

    private static RollupView Rollup(LeafRollup? rollup)
        => rollup is null
            ? new RollupView(0, 0, 0, 0, 0, 0, 0, 0)
            : new RollupView(
                rollup.Total,
                rollup.Done,
                rollup.InProgress,
                rollup.Review,
                rollup.Blocked,
                rollup.Open,
                rollup.Cancelled,
                rollup.Superseded);
}
