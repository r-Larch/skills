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
/// doc 15 §7: the hot reads with and without row-level security, against a database that already holds data — the
/// live-data copy, not a seed. Opt-in, and pointed at that database explicitly:
/// <c>dotnet test mcp/WorkState.slnx --filter FullyQualifiedName~RlsPerfTests -e WORKSTATE_RLS_PERF="Host=…"</c>.
/// The same real query methods run as the owner (no policy applies) and as the tenant role in the largest
/// workspace, interleaved call by call. Wall time per call includes the tenant transaction's extra round trip;
/// the server's own execution time per statement comes from <c>pg_stat_statements</c> when the server loads it.
/// </summary>
public sealed class RlsPerfTests
{
    private const int Rounds = 60;

    private static string? Target => Environment.GetEnvironmentVariable("WORKSTATE_RLS_PERF");

    public static bool Enabled => !string.IsNullOrWhiteSpace(Target);

    [Fact(Skip = "opt-in: set WORKSTATE_RLS_PERF to the live-data copy's connection string", SkipUnless = nameof(Enabled))]
    public async Task Rls_the_hot_reads_cost_the_same_with_and_without_row_level_security()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        await using var ownerSource = WorkStateDataSource.Create(Target!);
        await using var tenantSource = WorkStateDataSource.Create(Target!);
        WorkStateConnections.RunUnscopedAsOwner(ownerSource);

        Guid ws, run, project;
        bool statements;
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
            statements = await connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements');");
            if (statements)
            {
                await connection.ExecuteAsync("SELECT pg_stat_statements_reset();");
            }
        }

        var modes = new (string Name, NpgsqlDataSource Source, Guid? Scope)[]
        {
            ("owner", ownerSource, null),
            ("tenant", tenantSource, ws),
        };

        var timings = new Dictionary<(string Label, string Mode), List<double>>();
        foreach (var mode in modes)
        {
            foreach (var (label, _) in Calls(mode.Source, ws, run, project))
            {
                timings[(label, mode.Name)] = [];
            }
        }

        for (var round = -3; round < Rounds; round++)
        {
            // Alternate which mode goes first, so neither always meets the warmer cache.
            foreach (var mode in round % 2 == 0 ? modes : modes.Reverse())
            {
                using var scope = mode.Scope is { } tenant ? TenantScope.Enter(tenant) : null;
                foreach (var (label, call) in Calls(mode.Source, ws, run, project))
                {
                    var watch = Stopwatch.StartNew();
                    await call();
                    watch.Stop();
                    if (round >= 0)
                    {
                        timings[(label, mode.Name)].Add(watch.Elapsed.TotalMilliseconds);
                    }
                }
            }
        }

        var report = new StringBuilder();
        report.AppendLine($"workspace {ws}, run {run}, project {project}; {Rounds} rounds, median (p90) wall ms per call");
        report.AppendLine($"{"query",-26} {"owner",16} {"tenant (RLS)",16} {"delta",8}");
        foreach (var label in timings.Keys.Select(k => k.Label).Distinct())
        {
            var owner = timings[(label, "owner")];
            var tenant = timings[(label, "tenant")];
            report.AppendLine(
                $"{label,-26} {Median(owner),8:F3} ({P90(owner),5:F2}) {Median(tenant),8:F3} ({P90(tenant),5:F2}) "
                + $"{(Median(tenant) / Median(owner) - 1) * 100,7:F1}%");
        }

        if (statements)
        {
            await using var connection = await ownerSource.OpenConnectionAsync(Ct);
            var rows = await connection.QueryAsync<(string Role, long Calls, double Mean, string Query)>(
                $"""
                 SELECT r.rolname, s.calls, s.mean_exec_time, left(regexp_replace(s.query, '\s+', ' ', 'g'), 90)
                 FROM   pg_stat_statements s JOIN pg_roles r ON r.oid = s.userid
                 WHERE  s.calls >= {Rounds} AND s.query NOT LIKE '%set_config%' AND s.query NOT LIKE '%pg_stat_statements%'
                 ORDER  BY s.query, r.rolname;
                 """);

            report.AppendLine();
            report.AppendLine("server execution, mean ms per statement (pg_stat_statements)");
            foreach (var row in rows)
            {
                report.AppendLine($"{row.Role,-14} {row.Calls,6} {row.Mean,9:F4}  {row.Query}");
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "rls-perf.txt");
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
            ("threads/workspace", () => queries.OpenThreadsAsync(ws, null, Ct)),
            ("attention.done-unverified", () => board.DoneUnverifiedAsync(project, Ct)),
            ("journal/head", () => api.EventsAsync(ws, null, false, 0, 51, false, null, Ct)),
        ];
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private static double P90(List<double> values) => values.Order().ElementAt(values.Count * 9 / 10);
}
