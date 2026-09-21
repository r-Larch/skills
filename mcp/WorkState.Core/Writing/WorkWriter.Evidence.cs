using System.Text.Json;
using Dapper;
using Npgsql;
using WorkState.Core.Domain;
using WorkState.Core.Rendering;

namespace WorkState.Core.Writing;

/// <summary>doc 01 §3.4 — the report as its existing schema, so the format stops being prose.</summary>
public sealed record ReportArgs(
    string Status,
    string[]? Files = null,
    string? Verify = null,
    string? RedCheck = null,
    string? Deviations = null,
    string[]? Assumptions = null,
    string? Risks = null,
    string? NotDone = null);

public sealed partial class WorkWriter
{
    /// <summary>
    /// doc 01 §3.4. <c>result_line</c> is the actual line the command printed, never an exit code —
    /// an <c>exit 0</c> at the end of a pipe belongs to the <c>tail</c>, not to the build.
    /// <para>
    /// <c>run_by</c> comes from the token when the caller is a worker, never from the argument. That
    /// is the entire reason a worker may hold this tool: its evidence is recorded as a worker's, so
    /// it can never satisfy its own item's <c>done</c> guard (doc 07 §3 condition 2).
    /// </para>
    /// </summary>
    public async Task<WriteResult> VerifyAsync(
        Guid workspaceId,
        string? reference,
        Actor actor,
        string? command,
        string? resultLine,
        bool passed,
        string? runBy,
        string? counts,
        string? redCheck,
        CancellationToken cancellationToken = default)
    {
        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        if (string.IsNullOrWhiteSpace(resultLine))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_verify({scope.Ref}) without a result_line.",
                "result_line is the line the command actually printed — \"Passed! 187/187\" — because "
                + "an exit code at the end of a pipe belongs to the last command in it, not to the suite",
                $"work_verify(ref:\"{scope.Ref}\", command:\"…\", result_line:\"Passed! 187/187\", "
                + "run_by:\"orchestrator\")"));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);
        var runner = actor.RunnerFor(runBy);

        const string sql =
            """
            INSERT INTO verification
                (id, workspace_id, item_id, command, result_line, passed, counts, run_by, red_check)
            VALUES (@id, @workspace, @item, @command, @result, @passed, @counts::jsonb,
                    @runBy::verify_runner, @redCheck::jsonb);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = Guid.CreateVersion7(),
                workspace = item.WorkspaceId,
                item = item.Id,
                command = Blank(command) ? item.VerifyCmd : command!.Trim(),
                result = Glyph.Clip(resultLine, 400),
                passed,
                counts = Blank(counts) ? null : counts,
                runBy = Pg.Label(runner),
                redCheck = Blank(redCheck) ? null : JsonSerializer.Serialize(new { note = redCheck }),
            },
            transaction,
            cancellationToken: cancellationToken));

        await HeartbeatAsync(
            connection,
            transaction,
            item.Id,
            actor,
            await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken),
            cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "verified",
            $"{item.Ref} {(passed ? "green" : "RED")} by {Pg.Label(runner)}: {Glyph.Clip(resultLine, 40)}",
            new { command, result_line = resultLine, passed, run_by = Pg.Label(runner) },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var lines = new List<string>
        {
            $"OK  {item.Ref}  verification recorded {(passed ? "✓" : "✗")} by {Pg.Label(runner)}",
            $"    result  {Glyph.Clip(resultLine, 88)}",
        };

        if (runner == VerifyRunner.Worker)
        {
            lines.Add("    note    recorded as run_by=worker, from your token. A worker's own evidence");
            lines.Add("            never satisfies an item's done guard — that needs an orchestrator run.");
            lines.Add(string.Empty);
            lines.Add($"Next: work_report(ref:\"{item.Ref}\", status:\"done\", …) and hand back; "
                    + "closing is the orchestrator's call.");
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add(passed
                ? $"Next: work_status(ref:\"{item.Ref}\", to:\"done\")."
                : $"Next: work_status(ref:\"{item.Ref}\", to:\"in_progress\") — this one is red.");
        }

        return WriteResult.Ok(string.Join("\n", lines));
    }

    /// <summary>
    /// doc 01 §3.4. The attempt and the report, plus an <c>assumption</c> row per line of
    /// <c>assumptions[]</c> — the field the skills call the highest-value line in the format, which
    /// is the point of taking the report as a schema rather than as text.
    /// </summary>
    public async Task<WriteResult> ReportAsync(
        Guid workspaceId,
        string? reference,
        Actor actor,
        ReportArgs args,
        CancellationToken cancellationToken = default)
    {
        var (scope, unresolved) = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (scope is null)
        {
            return WriteResult.No(unresolved!);
        }

        if (Blank(args.Status))
        {
            return WriteResult.No(Refusal.Blocked(
                $"work_report({scope.Ref}) without a status.",
                "status is the first line of the report format: done, partial or blocked",
                $"work_report(ref:\"{scope.Ref}\", status:\"partial\", files:[…], verify:\"…\", "
                + "not_done:\"…\")"));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var item = await LoadAsync(connection, transaction, scope.Id, cancellationToken);

        const string attemptSql =
            """
            INSERT INTO attempt (id, item_id, n, agent_name, agent_kind, outcome, ended_at)
            SELECT @id, @item, coalesce(max(n), 0) + 1, @agent, @role, @outcome, now()
            FROM   attempt WHERE item_id = @item
            RETURNING n;
            """;

        var attemptId = Guid.CreateVersion7();
        var n = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            attemptSql,
            new
            {
                id = attemptId,
                item = item.Id,
                agent = actor.Name,
                role = Pg.Label(actor.Role),
                outcome = args.Status.Trim(),
            },
            transaction,
            cancellationToken: cancellationToken));

        const string reportSql =
            """
            INSERT INTO report (id, attempt_id, status, files, deviations, risks, not_done, raw)
            VALUES (@id, @attempt, @status, @files::jsonb, @deviations, @risks, @notDone, @raw);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            reportSql,
            new
            {
                id = Guid.CreateVersion7(),
                attempt = attemptId,
                status = args.Status.Trim(),
                files = JsonSerializer.Serialize(args.Files ?? []),
                deviations = Blank(args.Deviations) ? null : args.Deviations,
                risks = Blank(args.Risks) ? null : args.Risks,
                notDone = Blank(args.NotDone) ? null : args.NotDone,
                raw = Raw(args),
            },
            transaction,
            cancellationToken: cancellationToken));

        var opened = new List<string>();
        foreach (var statement in (args.Assumptions ?? []).Where(a => !Blank(a)))
        {
            opened.Add(await AssumeAsync(
                connection,
                transaction,
                item,
                actor,
                statement.Trim(),
                EvidenceLevel.Asserted,
                ReversalCost.Medium,
                null,
                cancellationToken));
        }

        await HeartbeatAsync(
            connection,
            transaction,
            item.Id,
            actor,
            await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken),
            cancellationToken);

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "reported",
            $"{item.Ref} attempt {n} reported {args.Status.Trim()} by {actor.Name}",
            new { attempt = n, status = args.Status.Trim(), assumptions = opened },
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var lines = new List<string>
        {
            $"OK  {item.Ref}  attempt {n} recorded, status {args.Status.Trim()}",
        };

        if (opened.Count > 0)
        {
            lines.Add($"    opened  {opened.Count} assumption(s) from assumptions[]: "
                    + $"{string.Join(", ", opened)}");
            lines.Add($"            work_assume(ref:\"{opened[0]}\", resolve:\"confirmed\", "
                    + "evidence:\"…\") when one settles");
        }

        lines.Add(string.Empty);
        lines.Add($"Next: work_verify(ref:\"{item.Ref}\", command:\"…\", result_line:\"…\") — "
                + "a report is a claim, not evidence.");

        return WriteResult.Ok(string.Join("\n", lines));
    }

    private static string Raw(ReportArgs args) => string.Join(
        "\n",
        $"STATUS: {args.Status.Trim()}",
        $"FILES: {string.Join(" · ", args.Files ?? [])}",
        $"VERIFY: {args.Verify ?? string.Empty}",
        $"RED-CHECK: {args.RedCheck ?? string.Empty}",
        $"DEVIATIONS: {args.Deviations ?? string.Empty}",
        $"ASSUMPTIONS: {string.Join(" · ", args.Assumptions ?? [])}",
        $"RISKS: {args.Risks ?? string.Empty}",
        $"NOT DONE: {args.NotDone ?? string.Empty}");

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);
}
