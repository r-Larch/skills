using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkState.Core.Data;
using WorkState.Core.Queries;
using Xunit;
using static WorkState.Tests.Store.Tokens;

namespace WorkState.Tests.Store;

/// <summary>
/// doc 03 task 1.4: the hot reads, measured against a seeded ~5,000-item workspace (doc 15). The
/// plans come from auto_explain on the real query methods, so what is measured is the statement the
/// server sends with the values it binds, not a copy of its SQL. Opt-in, because the seed and the
/// repeated runs cost far more than any other test:
/// <c>dotnet test mcp/WorkState.slnx --filter FullyQualifiedName~QueryPlanTests -e WORKSTATE_EXPLAIN=1</c>.
/// Writes every plan to <c>explain-5k.txt</c> beside the test assembly.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class QueryPlanTests(PostgresFixture fixture)
{
    private const int Repeats = 5;

    /// <summary>The doc 15 budget: a hot read over this seed above it needs an index or a rewrite.</summary>
    private const double BudgetMs = 50;

    public static bool Enabled => Environment.GetEnvironmentVariable("WORKSTATE_EXPLAIN") == "1";

    [Fact(Skip = "opt-in: set WORKSTATE_EXPLAIN=1", SkipUnless = nameof(Enabled))]
    public async Task The_hot_reads_are_explained_against_a_5000_item_workspace()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var database = $"explain_{Guid.CreateVersion7():n}";
        await using (var admin = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database};");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = database,
            CommandTimeout = 300,
        }.ConnectionString;

        try
        {
            await using (var context = new WorkStateDbContext(WorkStateDbContextOptions.Build(connectionString)))
            {
                await context.Database.MigrateAsync(Ct);
            }

            PlanWorld world;
            await using (var seedSource = WorkStateDataSource.Create(connectionString))
            await using (var connection = await seedSource.OpenConnectionAsync(Ct))
            {
                world = await PlanSeed.CreateAsync(connection, Ct);                await connection.ExecuteAsync("VACUUM ANALYZE;");
                await connection.ExecuteAsync("LOAD 'auto_explain';");
                await connection.ExecuteAsync(
                    $"""
                     ALTER DATABASE {database} SET session_preload_libraries = 'auto_explain';
                     ALTER DATABASE {database} SET auto_explain.log_min_duration = 0;
                     ALTER DATABASE {database} SET auto_explain.log_analyze = on;
                     ALTER DATABASE {database} SET auto_explain.log_buffers = on;
                     ALTER DATABASE {database} SET auto_explain.log_timing = on;
                     ALTER DATABASE {database} SET auto_explain.log_format = 'json';
                     """);
            }

            var report = await MeasureAsync(database, connectionString, world);
            var path = Path.Combine(AppContext.BaseDirectory, "explain-5k.txt");
            await File.WriteAllTextAsync(path, report.Text, Ct);
            TestContext.Current.TestOutputHelper?.WriteLine(report.Summary);
            TestContext.Current.TestOutputHelper?.WriteLine($"full plans: {path}");

            Assert.All(report.Labels, label => Assert.True(label.Value > 0, $"{label.Key}: no plan captured"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = await fixture.DataSource.OpenConnectionAsync(Ct);
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE);");
        }
    }

    private sealed record Report(string Summary, string Text, IReadOnlyDictionary<string, int> Labels);

    private sealed record Plan(string Label, int Run, double DurationMs, double JitMs, string QueryText, JsonElement Root);

    private async Task<Report> MeasureAsync(string database, string connectionString, PlanWorld world)
    {
        await using var source = WorkStateDataSource.Create(connectionString);
        var queries = new WorkStateQueries(source);
        var board = new BoardQueries(source);
        var api = new ApiQueries(source);

        var (run, project, ws) = (world.BusiestRunId, world.BusiestProjectId, world.WorkspaceId);
        var equivalent = await RollupEquivalence.AssertSameAsync(source, ws);

        long maxSeq;
        Guid[] addresseesRun;
        Guid[] addresseesProject;
        await using (var connection = await source.OpenConnectionAsync(Ct))
        {
            maxSeq = await connection.ExecuteScalarAsync<long>("SELECT max(seq) FROM event;");
        }

        addresseesRun = [.. (await board.UnconsumedNotesAsync(ws, run, Ct, openAddresseesOnly: true))
            .Where(n => n.ToItemId is not null).Select(n => n.ToItemId!.Value).Distinct()];
        addresseesProject = [.. (await board.UnconsumedNotesAsync(ws, project, Ct, openAddresseesOnly: true))
            .Where(n => n.ToItemId is not null).Select(n => n.ToItemId!.Value).Distinct()];

        var (p, r) = (world.BusiestProjectRef, world.BusiestRunRef);
        var measured = new (string Label, Func<Task> Call)[]
        {
            ("frontier/run", () => queries.ReadyFrontierAsync(run, Ct)),
            ("frontier/project", () => queries.ReadyFrontierAsync(project, Ct)),
            ("rollup/run", () => queries.RollupAsync(run, Ct)),
            ("rollup/project", () => queries.RollupAsync(project, Ct)),
            ("default-scope/workspace", () => board.DefaultScopeAsync(ws, Ct)),
            ("default-scope/project", () => board.DefaultScopeAsync(ws, project, Ct)),
            ("resolve/bare", () => board.ResolveAsync(ws, "3.4", Ct)),
            ("resolve/run", () => board.ResolveAsync(ws, $"{r}/3.4", Ct)),
            ("resolve/project-run", () => board.ResolveAsync(ws, $"{p}/{r}/3.4", Ct)),
            ("resolve/project-fallback", () => board.ResolveAsync(ws, $"{p}/B-3", Ct)),
            ("subtree/run", () => queries.SubtreeAsync(run, Ct)),
            ("subtree/project", () => queries.SubtreeAsync(project, Ct)),
            ("board/run", () => api.BoardAsync(run, Ct)),
            ("board/project", () => api.BoardAsync(project, Ct)),
            ("threads/run", () => queries.OpenThreadsAsync(ws, run, Ct)),
            ("threads/workspace", () => queries.OpenThreadsAsync(ws, null, Ct)),
            ("search/two-words", () => queries.SearchAsync(ws, "flaky migration", 20, Ct)),
            ("search/three-words", () => queries.SearchAsync(ws, "frontier rollup planner", 20, Ct)),
            ("search/two-words+scope-run", () => queries.SearchAsync(ws, "flaky migration", 20, run, false, Ct)),
            ("search/two-words+scope-project", () => queries.SearchAsync(ws, "flaky migration", 20, project, false, Ct)),
            ("search/items-only (refusal nearby)", () => queries.SearchAsync(ws, "flaky migration", 3, null, true, Ct)),
            ("attention.expired-claims/run", () => board.ExpiredClaimsAsync(run, Ct)),
            ("attention.expired-claims/project", () => board.ExpiredClaimsAsync(project, Ct)),
            ("attention.blocker-findings/run", () => board.BlockerFindingsAsync(run, Ct)),
            ("attention.blocker-findings/project", () => board.BlockerFindingsAsync(project, Ct)),
            ("attention.ungated-phases/run", () => board.UngatedPhasesAsync(run, Ct)),
            ("attention.ungated-phases/project", () => board.UngatedPhasesAsync(project, Ct)),
            ("attention.done-unverified/run", () => board.DoneUnverifiedAsync(run, Ct)),
            ("attention.done-unverified/project", () => board.DoneUnverifiedAsync(project, Ct)),
            ("attention.assumptions/run", () => board.AssumptionsAsync(ws, run, true, Ct)),
            ("attention.assumptions/project", () => board.AssumptionsAsync(ws, project, true, Ct)),
            ("attention.notes/run", () => board.UnconsumedNotesAsync(ws, run, Ct, openAddresseesOnly: true)),
            ("attention.notes/project", () => board.UnconsumedNotesAsync(ws, project, Ct, openAddresseesOnly: true)),
            ("attention.facts/run", () => board.FactsAsync(addresseesRun, Ct)),
            ("attention.facts/project", () => board.FactsAsync(addresseesProject, Ct)),
            ("attention.stale/run", () => board.StaleInProgressAsync(run, 4, Ct)),
            ("attention.stale/project", () => board.StaleInProgressAsync(project, 4, Ct)),
            ("journal/head", () => api.EventsAsync(ws, null, false, 0, 51, false, null, Ct)),
            ("journal/before", () => api.EventsAsync(ws, null, false, 0, 51, false, null, Ct, maxSeq - 5000)),
            ("journal/before-run", () => api.EventsAsync(ws, run, false, 0, 51, false, null, Ct, maxSeq - 5000)),
            ("journal/before-run-deep", () => api.EventsAsync(ws, run, false, 0, 51, false, null, Ct, maxSeq / 10)),
        };

        // Npgsql loads its type catalogue on the first physical connection; keep that out of the plans.
        await using (var connection = await source.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync("SELECT 1;");
        }

        foreach (var (label, call) in measured)
        {
            for (var i = 1; i <= Repeats; i++)
            {
                await MarkAsync(source, database, $"{label}#{i}");
                await call();
            }
        }

        await MarkAsync(source, database, "end");

        var log = string.Empty;
        for (var attempt = 0; attempt < 40 && !log.Contains($"EXPLAIN-MARK {database} end"); attempt++)
        {
            await Task.Delay(250, Ct);
            log = await fixture.ServerLogAsync(Ct);
        }

        var plans = Parse(log, database);
        var report = Render(world, measured.Select(m => m.Label).ToList(), plans);
        var proof = "ROLLUP EQUIVALENCE (rows identical to the item_rollup form): "
                    + string.Join(", ", equivalent.Select(e => $"{e.Key} {e.Value}"));
        return report with { Summary = $"{proof}\n\n{report.Summary}", Text = $"{proof}\n\n{report.Text}" };
    }

    private static async Task MarkAsync(NpgsqlDataSource source, string database, string label)
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync($"DO $$ BEGIN RAISE LOG 'EXPLAIN-MARK {database} {label}'; END $$;");
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(\.\d+)? \S+ \[\d+\] ", RegexOptions.Multiline)]
    private static partial Regex EntryStart();

    [GeneratedRegex(@"LOG:  duration: ([0-9.]+) ms  plan:\s*")]
    private static partial Regex Duration();

    private static List<Plan> Parse(string log, string database)
    {
        var plans = new List<Plan>();
        var starts = EntryStart().Matches(log).Select(m => m.Index).Append(log.Length).ToList();
        var marker = $"LOG:  EXPLAIN-MARK {database} ";
        string? label = null;
        var run = 0;

        for (var i = 0; i < starts.Count - 1; i++)
        {
            var entry = log[starts[i]..starts[i + 1]];
            var at = entry.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                var name = entry[(at + marker.Length)..].Split('\n')[0].Trim();
                var hash = name.LastIndexOf('#');
                (label, run) = hash < 0 ? (null, 0) : (name[..hash], int.Parse(name[(hash + 1)..], CultureInfo.InvariantCulture));
                continue;
            }

            if (label is null || Duration().Match(entry) is not { Success: true } duration)
            {
                continue;
            }

            using var json = JsonDocument.Parse(entry[(duration.Index + duration.Length)..]);
            var jit = json.RootElement.TryGetProperty("JIT", out var compiled)
                      && compiled.TryGetProperty("Timing", out var timing)
                ? Num(timing, "Total")
                : 0;
            plans.Add(new Plan(
                label,
                run,
                double.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture),
                jit,
                json.RootElement.GetProperty("Query Text").GetString() ?? string.Empty,
                json.RootElement.GetProperty("Plan").Clone()));
        }

        return plans;
    }

    private static Report Render(PlanWorld world, IReadOnlyList<string> labels, IReadOnlyList<Plan> plans)
    {
        var summary = new StringBuilder();
        var text = new StringBuilder();
        var captured = new Dictionary<string, int>();

        summary.AppendLine("SEED");
        foreach (var (table, count) in world.Counts)
        {
            summary.AppendLine($"  {table,-24} {count,8}");
        }

        summary.AppendLine($"  busiest run: {world.BusiestProjectRef}/{world.BusiestRunRef}");
        summary.AppendLine();
        summary.AppendLine(
            $"{"query",-38} {"median ms",10} {"min",8} {"max",8} {"jit ms",7} {"stmts",5} {"rows",7} {"hit",7} {"read",6}  seq scans (rows examined)");

        foreach (var label in labels)
        {
            var runs = plans.Where(p => p.Label == label).GroupBy(p => p.Run).OrderBy(g => g.Key).ToList();
            captured[label] = runs.Count;
            if (runs.Count == 0)
            {
                summary.AppendLine($"{label,-38} no plan captured");
                continue;
            }

            var totals = runs.Select(g => (Run: g.Key, Ms: g.Sum(p => p.DurationMs))).OrderBy(t => t.Ms).ToList();
            var median = totals[totals.Count / 2];
            var chosen = runs.Single(g => g.Key == median.Run).ToList();
            var main = chosen.MaxBy(p => p.DurationMs)!;
            var top = main.Root;
            var seqScans = chosen.SelectMany(p => Nodes(p.Root))
                .Where(n => Str(n, "Node Type") == "Seq Scan")
                .Select(n => $"{Str(n, "Relation Name")}:{Num(n, "Actual Loops") * (Num(n, "Actual Rows") + Num(n, "Rows Removed by Filter")):0}")
                .ToList();

            summary.AppendLine(
                $"{label,-38} {median.Ms,10:0.000} {totals[0].Ms,8:0.000} {totals[^1].Ms,8:0.000} {chosen.Sum(p => p.JitMs),7:0.0} {chosen.Count,5} "
                + $"{Num(top, "Actual Rows"),7:0} {chosen.Sum(p => Num(p.Root, "Shared Hit Blocks")),7:0} "
                + $"{chosen.Sum(p => Num(p.Root, "Shared Read Blocks")),6:0}  "
                + (seqScans.Count == 0 ? "-" : string.Join(", ", seqScans))
                + (median.Ms > BudgetMs ? "   OVER BUDGET" : string.Empty));

            text.AppendLine($"=== {label}  (median of {totals.Count}: {median.Ms:0.000} ms)");
            foreach (var plan in chosen)
            {
                text.AppendLine($"--- {plan.DurationMs:0.000} ms, of which JIT {plan.JitMs:0.000} ms");
                text.AppendLine(plan.QueryText.Trim());
                text.AppendLine("--- plan");
                Describe(plan.Root, 0, text);
            }

            text.AppendLine();
        }

        return new Report(summary.ToString(), summary + Environment.NewLine + text, captured);
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray().SelectMany(Nodes))
            {
                yield return child;
            }
        }
    }

    private static void Describe(JsonElement node, int depth, StringBuilder text)
    {
        var line = new StringBuilder(new string(' ', depth * 2));
        line.Append(Str(node, "Node Type"));
        if (Str(node, "Parent Relationship") is "SubPlan" or "InitPlan" && Str(node, "Subplan Name") is { Length: > 0 } sub)
        {
            line.Append($" [{sub}]");
        }

        if (Str(node, "Relation Name") is { Length: > 0 } relation)
        {
            line.Append($" on {relation}");
        }

        if (Str(node, "Index Name") is { Length: > 0 } index)
        {
            line.Append($" using {index}");
        }

        line.Append(CultureInfo.InvariantCulture,
            $"  cost={Num(node, "Total Cost"):0} est={Num(node, "Plan Rows"):0} rows={Num(node, "Actual Rows"):0} loops={Num(node, "Actual Loops"):0} time={Num(node, "Actual Total Time"):0.###}ms");
        line.Append(CultureInfo.InvariantCulture,
            $" hit={Num(node, "Shared Hit Blocks"):0} read={Num(node, "Shared Read Blocks"):0}");
        if (Num(node, "Rows Removed by Filter") is > 0 and var removed)
        {
            line.Append(CultureInfo.InvariantCulture, $" removed={removed:0}");
        }

        foreach (var key in new[] { "Index Cond", "Recheck Cond", "Hash Cond", "Merge Cond", "Join Filter", "Filter", "Sort Key" })
        {
            if (node.TryGetProperty(key, out var value))
            {
                var shown = value.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", value.EnumerateArray().Select(v => v.GetString()))
                    : value.GetString() ?? string.Empty;
                line.Append($"  {key}: {(shown.Length > 140 ? shown[..140] + "…" : shown)}");
            }
        }

        text.AppendLine(line.ToString());
        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                Describe(child, depth + 1, text);
            }
        }
    }

    private static string? Str(JsonElement node, string key)
        => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Num(JsonElement node, string key)
        => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}
