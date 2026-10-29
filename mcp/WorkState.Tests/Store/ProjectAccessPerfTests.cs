using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Queries;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// doc 15 §8: <see cref="RlsPerfTests"/>' hot reads as the owner, as a static token (the tenant role with no user) and
/// as a member (the tenant role with the user who is a member of the most projects of the largest workspace), against
/// a database that already holds data. Opt-in: <c>-e WORKSTATE_PROJECT_ACCESS_PERF="Host=…"</c>. pg_stat_statements
/// keys a statement by its role and the static token and the member share one, so each mode runs as a pass of its
/// own, its server time read before the next pass resets it.
/// </summary>
public sealed class ProjectAccessPerfTests
{
    private const int Rounds = 60;

    private static string? Target => Environment.GetEnvironmentVariable("WORKSTATE_PROJECT_ACCESS_PERF");

    public static bool Enabled => !string.IsNullOrWhiteSpace(Target);

    [Fact(Skip = "opt-in: set WORKSTATE_PROJECT_ACCESS_PERF to the live-data copy's connection string", SkipUnless = nameof(Enabled))]
    public async Task ProjectAccess_the_hot_reads_as_a_member_cost_what_they_cost_as_the_tenant()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        await using var ownerSource = WorkStateDataSource.Create(Target!);
        await using var tenantSource = WorkStateDataSource.Create(Target!);
        WorkStateConnections.RunUnscopedAsOwner(ownerSource);

        Guid ws, run, project, member;
        int policies;
        await using (var connection = await ownerSource.OpenConnectionAsync(Ct))
        {
            ws = await connection.ExecuteScalarAsync<Guid>(
                "SELECT workspace_id FROM item GROUP BY workspace_id ORDER BY count(*) DESC LIMIT 1;");
            (run, project) = await connection.QuerySingleAsync<(Guid, Guid)>(
                """
                SELECT r.id, r.parent_id
                FROM   item r
                WHERE  r.workspace_id = @ws AND r.kind = 'run'
                ORDER  BY (SELECT count(*) FROM item c WHERE c.ancestors @> ARRAY[r.id]) DESC
                LIMIT  1;
                """,
                new { ws });
            member = await connection.ExecuteScalarAsync<Guid>(
                "SELECT user_id FROM project_member WHERE workspace_id = @ws GROUP BY user_id ORDER BY count(*) DESC, user_id LIMIT 1;",
                new { ws });
            policies = await connection.ExecuteScalarAsync<int>("SELECT count(*)::int FROM pg_policy WHERE polname = 'project_access';");
        }

        var modes = new (string Name, NpgsqlDataSource Source, Func<IDisposable?> Scope)[]
        {
            ("owner", ownerSource, () => null),
            ("static", tenantSource, () => TenantScope.Enter(ws)),
            ("member", tenantSource, () => TenantScope.EnterAs(ws, member)),
        };

        var report = new StringBuilder();
        report.AppendLine($"workspace {ws}, run {run}, project {project}, member {member}; project_access policies: {policies}");
        report.AppendLine($"{Rounds} rounds after 3 warm-ups per mode; wall median (p90) ms per call, then server mean ms per statement");

        foreach (var mode in modes)
        {
            await using (var connection = await ownerSource.OpenConnectionAsync(Ct))
            {
                await connection.ExecuteAsync("SELECT pg_stat_statements_reset();");
            }

            var timings = new Dictionary<string, List<double>>();
            for (var round = -3; round < Rounds; round++)
            {
                using var scope = mode.Scope();
                foreach (var (label, call) in Calls(mode.Source, ws, run, project))
                {
                    var watch = Stopwatch.StartNew();
                    await call();
                    watch.Stop();
                    if (round >= 0)
                    {
                        (timings.TryGetValue(label, out var list) ? list : timings[label] = []).Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
            }

            report.AppendLine();
            report.AppendLine($"== {mode.Name}");
            foreach (var (label, values) in timings)
            {
                report.AppendLine($"{label,-26} {Median(values),8:F3} ({P90(values),6:F2})");
            }

            await using (var connection = await ownerSource.OpenConnectionAsync(Ct))
            {
                var rows = await connection.QueryAsync<(long Calls, double Mean, string Query)>(
                    $"""
                     SELECT s.calls, s.mean_exec_time, left(regexp_replace(s.query, '\s+', ' ', 'g'), 90)
                     FROM   pg_stat_statements s
                     WHERE  s.calls >= {Rounds} AND s.query NOT LIKE '%set_config%' AND s.query NOT LIKE '%pg_stat_statements%'
                     ORDER  BY s.query;
                     """);
                foreach (var row in rows)
                {
                    report.AppendLine($"  {row.Calls,6} {row.Mean,9:F4}  {row.Query}");
                }
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "project-access-perf.txt");
        await File.WriteAllTextAsync(path, report.ToString(), Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    private static (string Label, Func<Task> Call)[] Calls(NpgsqlDataSource source, Guid ws, Guid run, Guid project)
    {
        var queries = new WorkStateQueries(source);
        var board = new BoardQueries(source);
        var api = new ApiQueries(source);

        return
        [
            ("frontier/run", () => queries.ReadyFrontierAsync(run, Ct)),
            ("frontier/project", () => queries.ReadyFrontierAsync(project, Ct)),
            ("rollup/run", () => queries.RollupAsync(run, Ct)),
            ("rollup/project", () => queries.RollupAsync(project, Ct)),
            ("board/run", () => api.BoardAsync(run, Ct)),
            ("board/project", () => api.BoardAsync(project, Ct)),
            ("subtree/run", () => queries.SubtreeAsync(run, Ct)),
            ("subtree/project", () => queries.SubtreeAsync(project, Ct)),
            ("default-scope/workspace", () => board.DefaultScopeAsync(ws, Ct)),
            ("projects/workspace", () => board.ProjectsAsync(ws, Ct)),
            ("threads/workspace", () => queries.OpenThreadsAsync(ws, null, Ct)),
            ("search/two-words", () => queries.SearchAsync(ws, "flaky migration", 20, Ct)),
            ("journal/head", () => api.EventsAsync(ws, null, false, 0, 51, false, null, Ct)),
        ];
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private static double P90(List<double> values) => values.Order().ElementAt(values.Count * 9 / 10);
}
