using WorkState.Core.Queries;
using WorkState.Mcp;

namespace WorkState.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>Everything a human needs on landing, in one call.</summary>
    private static async Task<IResult> OverviewAsync(
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        var book = new V1Book(api, board);
        var projects = await board.ProjectsAsync(snapshot.WorkspaceId, cancellationToken);
        var scopes = projects.Select(p => p.Id).ToList();

        var runs = new Dictionary<Guid, IReadOnlyList<RunRow>>();
        foreach (var project in projects)
        {
            runs[project.Id] = await api.RunsAsync(project.Id, cancellationToken);
        }

        var allRuns = runs.Values.SelectMany(r => r).Select(r => r.Id).ToList();
        await book.ItemsAsync(scopes.Concat(allRuns), cancellationToken);

        var rollups = (await api.RollupsAsync([.. scopes, .. allRuns], cancellationToken)).ToDictionary(r => r.Id);
        var inbox = await InboxRowsAsync(snapshot, scopes, book, board, api, cancellationToken);
        var inboxByRun = inbox.Where(r => r.Run is not null).GroupBy(r => r.Run!.Id).ToDictionary(g => g.Key, g => g.Count());

        // The honesty strip, summed over projects exactly as work_audit counts it.
        int doneUnverified = 0, forced = 0, ungated = 0, followupsOpen = 0;
        foreach (var scope in scopes)
        {
            doneUnverified += (await board.DoneUnverifiedAsync(scope, cancellationToken)).Count;
            forced += (await board.ForcedTransitionsAsync(scope, cancellationToken)).Count;
            ungated += (await board.UngatedPhasesAsync(scope, cancellationToken)).Count;
            followupsOpen += await board.OpenFollowupsAsync(scope, cancellationToken);
        }

        var repos = (await board.ProjectReposAsync(scopes, cancellationToken)).ToLookup(r => r.ProjectId);
        var projectViews = new List<ProjectOverviewView>();
        foreach (var project in projects)
        {
            var runViews = new List<RunOverviewView>();
            foreach (var run in runs[project.Id])
            {
                var last = await board.LastVerificationAsync(run.Id, cancellationToken);
                runViews.Add(new RunOverviewView(
                    book.Item(run.Id),
                    Rollup(rollups.GetValueOrDefault(run.Id)),
                    run.Branch,
                    run.Worktree,
                    run.ActiveClaims,
                    run.LastEventAt,
                    inboxByRun.GetValueOrDefault(run.Id),
                    last is null ? null : new LastVerificationView(last.Passed, last.At)));
            }

            projectViews.Add(new ProjectOverviewView(
                book.Item(project.Id),
                Rollup(rollups.GetValueOrDefault(project.Id)),
                runViews,
                [.. repos[project.Id].Select(r => new RepoView(r.Kind, r.Key))],
                project.ArchivedAt,
                project.LastActivityAt,
                project.OpenRuns));
        }

        var claims = await book.ClaimsAsync(
            [.. (await api.HeldClaimsAsync(scopes, cancellationToken)).OrderByDescending(c => c.AcquiredAt).ThenBy(c => c.Id)],
            snapshot.AsOf,
            cancellationToken);

        return Results.Ok(new OverviewView(
            snapshot.AsOf,
            snapshot.Seq,
            projectViews,
            claims,
            inbox.Count,
            new HonestyView(doneUnverified, forced, ungated, followupsOpen)));
    }
}
