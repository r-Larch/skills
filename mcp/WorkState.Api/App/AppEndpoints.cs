using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using WorkState.Api.Auth;
using WorkState.Core.Board;
using WorkState.Core.Domain;
using WorkState.Core.Queries;
using WorkState.Core.Rendering;
using WorkState.Core.Writing;
using WorkState.Mcp;

namespace WorkState.Api.App;

/// <summary>
/// The human's read path (doc 09). Everything here reuses <see cref="BoardReader"/> and the queries
/// the MCP tools use; nothing here writes SQL of its own and nothing re-renders. If the dashboard
/// and an agent ever show different numbers, the dashboard is lying about what the agent sees.
/// </summary>
public static class AppEndpoints
{
    private const int JournalDefaultLimit = 50;
    private const int JournalMaxLimit = 500;

    public static void MapAppApi(this WebApplication app)
    {
        // ExcludeFromDescription keeps the operator's API out of the service's OpenAPI document
        // (doc 08 §4): that document describes /mcp to agents, and an endpoint an agent can never
        // reach does not belong in it.
        var api = app.MapGroup("/api").ExcludeFromDescription();

        MapSession(api);

        var guarded = api.MapGroup(string.Empty).RequireAuthorization(DashboardAuth.Policy);

        guarded.MapGet("/projects", ProjectsAsync);
        guarded.MapGet("/watch", WatchAsync);
        guarded.MapGet("/runs/{reference}/tree", TreeAsync);
        guarded.MapGet("/runs/{reference}/brief", BriefAsync);
        guarded.MapGet("/runs/{reference}/journal", JournalAsync);
        guarded.MapGet("/stream", EventStream.HandleAsync);
    }

    private static void MapSession(RouteGroupBuilder api)
    {
        // Signing in cannot require being signed in. Every other route in the group does.
        api.MapPost("/session", async (
            SessionRequest request,
            DashboardOptions dashboard,
            HttpContext context) =>
        {
            if (!dashboard.Matches(request.Username, request.Password))
            {
                return Results.Unauthorized();
            }

            await context.SignInAsync(
                DashboardAuth.SchemeName,
                DashboardAuth.Principal(request.Username!));

            return Results.NoContent();
        }).AllowAnonymous();

        api.MapDelete("/session", async (HttpContext context) =>
        {
            await context.SignOutAsync(DashboardAuth.SchemeName);
            return Results.NoContent();
        }).AllowAnonymous();
    }

    private static async Task<IResult> ProjectsAsync(
        BoardQueries board,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (await board.WorkspaceAsync(workspace.Slug, cancellationToken) is not { } workspaceId)
        {
            return Results.Ok(Array.Empty<ProjectView>());
        }

        var projects = await board.ProjectsAsync(workspaceId, cancellationToken);

        return Results.Ok(projects
            .Select(p => new ProjectView(p.Ref, p.Title, Status(p.Status), p.RunCount))
            .ToList());
    }

    /// <summary>
    /// doc 09 §5's landing view. Omitting <c>project</c> watches every project at once, which is
    /// the state an operator is actually in before they have chosen one.
    /// </summary>
    private static async Task<IResult> WatchAsync(
        [FromQuery] string? project,
        BoardReader reader,
        BoardQueries board,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (await board.WorkspaceAsync(workspace.Slug, cancellationToken) is not { } workspaceId)
        {
            return Results.Ok(Empty(project));
        }

        var scopes = new List<ScopeResult.Found>();

        if (string.IsNullOrWhiteSpace(project))
        {
            foreach (var row in await board.ProjectsAsync(workspaceId, cancellationToken))
            {
                var matches = await board.ResolveAsync(workspaceId, row.Ref, cancellationToken);
                if (matches.Count == 1)
                {
                    scopes.Add(new ScopeResult.Found(workspaceId, matches[0]));
                }
            }
        }
        else
        {
            if (await reader.ResolveAsync(workspace.Slug, project, cancellationToken)
                is not ScopeResult.Found found)
            {
                return Results.NotFound(new { error = $"no project with ref \"{project}\"" });
            }

            scopes.Add(found);
        }

        var leases = new List<LeaseView>();
        var needsYou = new List<AttentionView>();
        var doneUnverified = 0;
        var forced = 0;
        var ungated = 0;
        var followupsOpen = 0;

        foreach (var scope in scopes)
        {
            var id = scope.Item.Id;

            leases.AddRange((await board.LiveClaimsAsync(id, cancellationToken))
                .Select(c => new LeaseView(c.AgentName, c.ItemRef, c.QualifiedRef, c.Title, c.ExpiresAt)));

            needsYou.AddRange((await reader.AttentionAsync(scope, cancellationToken))
                .Select(a => new AttentionView(Rank(a.Rank), a.Ref, a.QualifiedRef, a.What, a.Call)));

            doneUnverified += (await board.DoneUnverifiedAsync(id, cancellationToken)).Count;
            forced += (await board.ForcedTransitionsAsync(id, cancellationToken)).Count;
            ungated += (await board.UngatedPhasesAsync(id, cancellationToken)).Count;
            followupsOpen += await board.OpenFollowupsAsync(id, cancellationToken);
        }

        return Results.Ok(new WatchView(
            string.IsNullOrWhiteSpace(project) ? null : project,
            DateTimeOffset.UtcNow,
            [.. leases.OrderByDescending(l => l.ExpiresAt)],
            [.. needsYou],
            new HonestyView(doneUnverified, forced, ungated, followupsOpen)));
    }

    /// <summary>
    /// The run tree with both numbers on every node. doc 09 §5: a phase the store calls closed over
    /// children that are not is a disagreement the UI must show, and the real corpus has it.
    /// </summary>
    private static async Task<IResult> TreeAsync(
        string reference,
        BoardReader reader,
        WorkStateQueries queries,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (await reader.ResolveAsync(workspace.Slug, reference, cancellationToken)
            is not ScopeResult.Found found)
        {
            return Results.NotFound(new { error = $"no item with ref \"{reference}\"" });
        }

        var subtree = await queries.SubtreeAsync(found.Item.Id, cancellationToken);
        if (subtree.Count == 0)
        {
            return Results.Ok(new TreeView(found.Item.Ref, found.Item.Title, []));
        }

        var root = subtree.First(n => n.Id == found.Item.Id);

        var nodes = subtree.Select(n =>
        {
            var total = (int)(n.OpenCount + n.DoingCount + n.BlockedCount
                              + n.ReviewCount + n.DoneCount);
            var done = (int)n.DoneCount;

            return new TreeNodeView(
                n.Ref,
                n.Kind,
                n.Title,
                Status(n.Status),
                n.Depth - root.Depth,
                done,
                (int)n.ReviewCount,
                total,
                Disagrees(n.Status, done, total));
        }).ToList();

        return Results.Ok(new TreeView(found.Item.Ref, found.Item.Title, nodes));
    }

    /// <summary>
    /// The brief as <c>text/plain</c>, byte-identical to what <c>work_brief</c> returns for the same
    /// scope. It is the agent's artifact and the human is reading a quotation of it, so it is not
    /// wrapped in JSON: a string inside an object is a copy, and a copy is a thing that can drift.
    /// </summary>
    private static async Task<IResult> BriefAsync(
        string reference,
        [FromQuery] string?
            @for,
        BoardReader reader,
        Housekeeping housekeeping,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (await reader.ResolveAsync(workspace.Slug, reference, cancellationToken)
            is not ScopeResult.Found found)
        {
            return Results.NotFound(new { error = $"no item with ref \"{reference}\"" });
        }

        // A write behind a GET, and deliberate: BriefSessionAsync runs doc 07 §5's sweep, which is
        // housekeeping the store owes itself rather than an operator action — a lease that lapsed
        // is released whoever happens to look. Serving the brief without it would return different
        // bytes from work_brief for the same scope, which is the one thing this endpoint exists to
        // make impossible.
        var brief = await reader.BriefSessionAsync(found, @for ?? "resume", housekeeping, cancellationToken);

        return Results.Text(brief, "text/plain");
    }

    private static async Task<IResult> JournalAsync(
        string reference,
        [FromQuery] long? since,
        [FromQuery] int? limit,
        BoardReader reader,
        BoardQueries board,
        WorkspaceOptions workspace,
        CancellationToken cancellationToken)
    {
        if (await reader.ResolveAsync(workspace.Slug, reference, cancellationToken)
            is not ScopeResult.Found found)
        {
            return Results.NotFound(new { error = $"no item with ref \"{reference}\"" });
        }

        var cursor = Math.Max(since ?? 0, 0);
        var take = Math.Clamp(limit ?? JournalDefaultLimit, 1, JournalMaxLimit);

        IReadOnlyList<EventRow> rows;
        bool more;

        if (cursor > 0)
        {
            // Catching up. Ascending from the cursor, one extra row to discover whether the page
            // was truncated — a client that has been away longer than the page must be told so
            // rather than left believing the gap it cannot see is not there.
            var page = await board.EventsAsync(
                found.WorkspaceId, found.Item.Id, cursor, take + 1, oldestFirst: true, cancellationToken);

            more = page.Count > take;
            rows = [.. page.Take(take)];
        }
        else
        {
            // First load. The newest page, reversed into the same ascending order, so the shape of
            // the response never depends on which call the client is making.
            var page = await board.EventsAsync(
                found.WorkspaceId, found.Item.Id, 0, take, oldestFirst: false, cancellationToken);

            more = false;
            rows = [.. page.Reverse()];
        }

        return Results.Ok(new JournalView(
            rows.Count == 0 ? cursor : rows[^1].Seq,
            more,
            [.. rows.Select(e => new JournalEntryView(
                e.Seq,
                e.ItemRef,
                e.At,
                e.Actor,
                Snake(e.Role.ToString()),
                e.Kind,
                e.Summary))]));
    }

    private static WatchView Empty(string? project)
        => new(project, DateTimeOffset.UtcNow, [], [], new HonestyView(0, 0, 0, 0));

    /// <summary>
    /// A container the store calls closed while children of it are not. Counted from the rollup,
    /// which excludes cancelled and superseded children — so this marks work that was abandoned
    /// without being cancelled, not work that was deliberately dropped.
    /// </summary>
    private static bool Disagrees(ItemStatus status, int done, int total)
        => total > 0
           && done < total
           && status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;

    /// <summary>The store's own vocabulary, not .NET's. <c>in_progress</c> is what the column holds,
    /// what the docs say and what an agent is told — the dashboard has no licence to rename it.</summary>
    private static string Status(ItemStatus status) => Snake(status.ToString());

    private static string Rank(AttentionRank rank) => Snake(rank.ToString());

    private static string Snake(string pascal)
    {
        var builder = new System.Text.StringBuilder(pascal.Length + 4);

        foreach (var character in pascal)
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
