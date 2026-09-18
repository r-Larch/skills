using System.Text.Json;
using System.Text.RegularExpressions;
using WorkState.Core.Domain;
using WorkState.Import.Markdown;
using WorkState.Import.Model;

namespace WorkState.Import.Extraction;

/// <summary>
/// One orchestration run directory read into <see cref="RunImport"/>. Every file becomes a doc with
/// its blocks verbatim; the typed readers run over the same blocks and are an index over that prose,
/// never a replacement for it (brief §1, doc 00 §4).
/// </summary>
public sealed partial class RunParser(RunImport run)
{
    private string? _currentPhaseRef;
    private string? _currentItemRef;
    private string _currentFile = "";
    private bool _isPlan;

    public void AddFile(string relativePath, string title, MarkdownFile file)
    {
        // Run-qualified, so "this shape occurs in N files" counts files and not file names: every run in
        // the corpus calls its ledger LEDGER.md.
        _currentFile = run.ProjectSlug + "/" + run.RunSlug + "/" + relativePath;
        _currentPhaseRef = null;
        _currentItemRef = null;

        var fileName = Path.GetFileName(relativePath);
        _isPlan = fileName.StartsWith("PLAN", StringComparison.OrdinalIgnoreCase)
                  || fileName.StartsWith("TASKS", StringComparison.OrdinalIgnoreCase);

        var doc = new DocImport
        {
            Slug = DocSlug(relativePath),
            Title = title,
            RelativePath = relativePath,
            ReplacementChars = file.ReplacementChars,
        };

        var seq = 0m;
        foreach (var block in file.Blocks)
        {
            seq += 1m;
            doc.Blocks.Add(new BlockImport(
                "b" + (block.Index + 1),
                seq,
                block.Heading,
                block.HeadingLevel is { } level ? (short)level : null,
                block.Body));
        }

        run.Docs.Add(doc);
        run.ReplacementChars += file.ReplacementChars;

        var isBrief = BriefRegex().IsMatch(Path.GetFileName(relativePath));
        if (isBrief)
        {
            ReadBrief(relativePath, doc);
        }

        var isConventions = Path.GetFileName(relativePath)
            .Equals("CONVENTIONS.md", StringComparison.OrdinalIgnoreCase);

        foreach (var block in file.Blocks)
        {
            ReadBlock(block, isConventions);
        }
    }

    public static string DocSlug(string relativePath)
        => relativePath.Replace('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Parents resolved once every file has been read, because a ledger names task <c>4.2b</c> long
    /// before the plan that opens phase 4 has necessarily been parsed.
    /// </summary>
    public void ResolveParents()
    {
        foreach (var item in run.Items.Values)
        {
            if (item.ParentRef is { } parent && !run.Items.ContainsKey(parent))
            {
                item.ParentRef = null;
            }

            if (item.ParentRef is null
                && item.Kind is "task" or "followup" or "finding"
                && Refs.PhaseOf(item.Ref) is { } phase
                && run.Items.ContainsKey("phase-" + phase))
            {
                item.ParentRef = "phase-" + phase;
            }

            if (item.ParentRef == item.Ref)
            {
                item.ParentRef = null;
            }
        }
    }

    private void ReadBlock(MarkdownBlock block, bool isConventions)
    {
        TrackContext(block);

        if (isConventions)
        {
            ReadConventionsSection(block);
        }

        var scan = MarkdownTable.Scan(block.Body);

        if (scan.AttachedRows > 0)
        {
            run.Unmapped.Record(
                "table-row-detached-from-its-header",
                "rows written below their table with prose in between, reunited by column count",
                scan.AttachedRows + " row(s) under " + MdText.Truncate(block.Heading ?? "(preamble)", 60),
                _currentFile);
        }

        foreach (var orphan in scan.OrphanRows)
        {
            run.Unmapped.Record(
                "pipe-row-belonging-to-no-table",
                "a row whose column count matches no table in its section",
                MdText.Truncate(orphan, 120),
                _currentFile);
        }

        foreach (var table in scan.Tables)
        {
            ReadTable(block, table);
        }

        ReadHeadingSection(block);
    }

    // ---------------------------------------------------------------- context

    /// <summary>
    /// A heading either opens a phase, closes a task, or is prose. The corpus writes a task's outcome
    /// into its heading — <c>### 4.1 IKnowledgeContext — ACCEPTED (commit `844f7983`)</c> — far more
    /// often than into a table, so the heading is a first-class row source, not decoration.
    /// </summary>
    private void TrackContext(MarkdownBlock block)
    {
        if (block.Heading is not { } heading)
        {
            return;
        }

        var phase = PhaseHeadingRegex().Match(heading);
        if (phase.Success)
        {
            var phaseRef = "phase-" + phase.Groups[1].Value;
            var item = run.Item(phaseRef, "phase");
            item.ParentRef ??= null;
            if (item.Title.Length == 0)
            {
                item.Title = MdText.Plain(heading);
            }

            _currentPhaseRef = phaseRef;
            _currentItemRef = null;
            return;
        }

        var plain = MdText.Plain(heading);
        if (!TaskHeadingRegex().IsMatch(plain) || Refs.ParseLabel(plain) is not { } parsed)
        {
            return;
        }

        var (reference, label) = parsed;
        var rest = Headline(label);
        var verdict = Vocabulary.ReadShoutedVerdict(label);

        var taskItem = run.Item(reference, "task");
        if (taskItem.Title.Length == 0)
        {
            taskItem.Title = MdText.Truncate(rest.Length > 0 ? rest : reference, 200);
        }

        taskItem.ParentRef ??= PhaseRefFor(reference);
        _currentItemRef = reference;

        if (verdict is null)
        {
            return;
        }

        ApplyVerdict(taskItem, verdict);
        run.Events.Add(new EventImport(
            reference,
            "judged",
            ActorRole.Orchestrator,
            "orchestrator",
            $"{reference}: {verdict.Token}",
            JsonSerializer.Serialize(new { verdict = verdict.Token, source = "heading", file = _currentFile })));

        if (MdText.CommitSha(heading) is { } sha)
        {
            run.Artifacts.Add(new ArtifactImport(reference, "commit", sha, "{}"));
        }
    }

    private string? PhaseRefFor(string reference)
        => Refs.PhaseOf(reference) is { } n && run.Items.ContainsKey("phase-" + n)
            ? "phase-" + n
            : _currentPhaseRef;

    private static void ApplyVerdict(ItemImport item, Verdict verdict)
    {
        item.Status = verdict.Status;
        item.Resolution = verdict.Resolution;
    }

    // ----------------------------------------------------------------- tables

    private void ReadTable(MarkdownBlock block, MarkdownTable table)
    {
        foreach (var row in table.Rows.Where(r => !r.ArityMatches))
        {
            run.Unmapped.Record(
                "malformed-row",
                $"{table.Signature} — {table.Header.Count} columns, row has {row.Cells.Count}",
                MdText.Truncate(row.Raw.Trim(), 120),
                _currentFile);
        }

        if (table.Has("assumption"))
        {
            ReadAssumptionTable(table);
            return;
        }

        if (table.Has("sabotage"))
        {
            ReadRedCheckTable(table);
            return;
        }

        if (table.Has("verdict", "state", "verified by me"))
        {
            ReadTaskLogTable(table);
            return;
        }

        if ((table.Has("done when", "verify") || table.Has("deliverable", "delivered", "outcome"))
            && table.Has("task", "#"))
        {
            ReadPlanTaskTable(table);
            return;
        }

        if (table.Has("finding") && table.Has("fix"))
        {
            ReadFindingTable(table);
            return;
        }

        if (table.Has("command") && table.Has("result"))
        {
            ReadCommandResultTable(table);
            return;
        }

        // A results table whose left column is what was checked rather than the command that checked it.
        // Counted under its own name because it is the same finding as the verify cells with no command,
        // and the two counts together are the evidence for doubt 1 in the report.
        if (table.Has("result", "observed") && !table.Has("command"))
        {
            run.Unmapped.Record(
                "verification-table-without-a-command-column",
                table.Signature,
                MdText.Truncate(block.Heading ?? table.Signature, 80),
                _currentFile);
            return;
        }

        run.Unmapped.Record(
            "table",
            table.Signature,
            MdText.Truncate(block.Heading ?? table.Signature, 80),
            _currentFile);
    }

    private void ReadAssumptionTable(MarkdownTable table)
    {
        var refColumn = table.Column("#", "ref", "id");
        var statementColumn = table.Column("assumption", "statement");
        var evidenceColumn = table.Column("evidence level", "evidence");
        var reversalColumn = table.Column("reversal cost", "reversal");
        var revisitColumn = table.Column("revisit when", "revisit");

        var ordinal = 0;
        foreach (var row in table.Rows)
        {
            ordinal++;
            var statement = MdText.Plain(MarkdownTable.Cell(row, statementColumn));
            if (statement.Length == 0)
            {
                continue;
            }

            var rawRef = MarkdownTable.Cell(row, refColumn);
            var reference = NormalizeAssumptionRef(rawRef, ordinal);

            var evidenceCell = MarkdownTable.Cell(row, evidenceColumn);
            var evidence = Vocabulary.ReadEvidence(evidenceCell);
            if (evidence is null && !MdText.IsEmpty(evidenceCell))
            {
                run.Unmapped.Record(
                    "assumption-evidence-token",
                    MdText.Truncate(MdText.Plain(evidenceCell), 40),
                    statement,
                    _currentFile);
            }

            var reversalCell = MarkdownTable.Cell(row, reversalColumn);
            var reversal = Vocabulary.ReadReversal(reversalCell);
            if (reversal is null)
            {
                run.Unmapped.Record(
                    "assumption-reversal-unreadable",
                    MdText.IsEmpty(reversalCell) ? "(empty cell)" : MdText.Truncate(MdText.Plain(reversalCell), 40),
                    statement,
                    _currentFile);
            }

            var resolved = MdText.IsStruckThrough(rawRef)
                           || MdText.IsStruckThrough(MarkdownTable.Cell(row, statementColumn))
                           || ResolvedRegex().IsMatch(statement);

            var state = !resolved
                ? AssumptionState.Open
                : FalsifiedRegex().IsMatch(statement)
                    ? AssumptionState.Disproven
                    : ObsoleteRegex().IsMatch(statement)
                        ? AssumptionState.Obsolete
                        : AssumptionState.Confirmed;

            var revisitCell = MarkdownTable.Cell(row, revisitColumn);
            var revisitRef = MdText.IsEmpty(revisitCell) ? null : Refs.Parse(revisitCell);

            run.Assumptions.Add(new AssumptionImport(
                reference,
                MdText.Truncate(statement, 4000),
                evidence ?? EvidenceLevel.Asserted,
                reversal ?? ReversalCost.Medium,
                state,
                revisitRef,
                state == AssumptionState.Open ? null : MdText.Date(statement) ?? DateTimeOffset.UtcNow,
                state == AssumptionState.Open ? null : MdText.Truncate(statement, 1000)));
        }
    }

    /// <summary>
    /// The corpus's own handle where it writes one — <c>A2</c>, <c>~~A1~~</c>, <c>3</c> — because a
    /// later ledger revisits an assumption by that handle and a re-import has to land on the same row.
    /// </summary>
    private static string NormalizeAssumptionRef(string? cell, int ordinal)
    {
        var plain = MdText.Plain(cell).Trim();
        if (plain.Length == 0)
        {
            return "A" + ordinal;
        }

        if (int.TryParse(plain, out var number))
        {
            return "A" + number;
        }

        return AssumptionHandleRegex().IsMatch(plain) ? plain : Refs.Slug(plain, 16);
    }

    private void ReadTaskLogTable(MarkdownTable table)
    {
        var refColumn = table.Column("#", "ref");
        var taskColumn = table.Column("task", "what", "item");
        var verdictColumn = table.Column("verdict", "state", "status");
        var commitColumn = table.Column("commit", "sha");
        var verifyColumn = table.Column("verify", "verified by me", "evidence");
        var noteColumn = table.Column("note", "notes");

        foreach (var row in table.Rows)
        {
            var labelCell = refColumn >= 0 && !Refs.IsOrdinal(MarkdownTable.Cell(row, refColumn))
                ? MarkdownTable.Cell(row, refColumn)
                : MarkdownTable.Cell(row, taskColumn);

            if (Refs.ParseLabel(labelCell) is not { } parsed)
            {
                continue;
            }

            var (reference, label) = parsed;

            var titleCell = taskColumn >= 0 && refColumn >= 0
                ? MdText.Plain(MarkdownTable.Cell(row, taskColumn))
                : table.Column("deliverable", "description", "summary") is var describe && describe >= 0
                    ? MdText.Plain(MarkdownTable.Cell(row, describe))
                    : label;

            var item = run.Item(reference, "task");
            if (item.Title.Length == 0)
            {
                item.Title = MdText.Truncate(titleCell.Length > 0 ? titleCell : label, 200);
            }

            item.ParentRef ??= PhaseRefFor(reference);

            var verdictCell = MarkdownTable.Cell(row, verdictColumn);
            var verdict = Vocabulary.ReadVerdict(verdictCell) ?? Vocabulary.ReadVerdict(titleCell);

            if (verdict is not null)
            {
                ApplyVerdict(item, verdict);
                run.Events.Add(new EventImport(
                    reference,
                    "judged",
                    ActorRole.Orchestrator,
                    "orchestrator",
                    $"{reference}: {verdict.Token}",
                    JsonSerializer.Serialize(new
                    {
                        verdict = verdict.Token,
                        note = MdText.Truncate(MdText.Plain(MarkdownTable.Cell(row, noteColumn)), 500),
                        file = _currentFile,
                    })));
            }
            else if (!MdText.IsEmpty(verdictCell))
            {
                run.Unmapped.Record(
                    "verdict-token",
                    MdText.Truncate(MdText.Plain(verdictCell), 40),
                    reference,
                    _currentFile);
            }

            if (MdText.CommitSha(MarkdownTable.Cell(row, commitColumn)) is { } sha)
            {
                run.Artifacts.Add(new ArtifactImport(reference, "commit", sha, "{}"));
            }

            var runBy = table.Has("verified by me") ? VerifyRunner.Orchestrator : VerifyRunner.Worker;
            AddVerification(item, MarkdownTable.Cell(row, verifyColumn), verdict, runBy);
        }
    }

    private void ReadPlanTaskTable(MarkdownTable table)
    {
        var refColumn = table.Column("#", "ref");
        var taskColumn = table.Column("task", "what");
        var describeColumn = table.Column("deliverable", "delivered", "outcome", "description", "summary");
        var verifyColumn = table.Column("verify");
        var criterionColumn = table.Column("done when", "done criteria");
        var reviewColumn = table.Column("review", "risk");

        foreach (var row in table.Rows)
        {
            var useRefColumn = refColumn >= 0 && !Refs.IsOrdinal(MarkdownTable.Cell(row, refColumn));
            var labelCell = useRefColumn ? MarkdownTable.Cell(row, refColumn) : MarkdownTable.Cell(row, taskColumn);
            if (Refs.ParseLabel(labelCell) is not { } parsed)
            {
                continue;
            }

            var (reference, label) = parsed;

            var item = run.Item(reference, "task");
            var titleCell = refColumn >= 0 && taskColumn >= 0 && taskColumn != refColumn
                ? MdText.Plain(MarkdownTable.Cell(row, taskColumn))
                : describeColumn >= 0
                    ? MdText.Plain(MarkdownTable.Cell(row, describeColumn))
                    : label;
            if (item.Title.Length == 0)
            {
                item.Title = MdText.Truncate(titleCell.Length > 0 ? titleCell : label, 200);
            }

            // What the row says was delivered is the task's own account of itself, not a second title.
            if (describeColumn >= 0 && describeColumn != taskColumn)
            {
                var delivered = MdText.Plain(MarkdownTable.Cell(row, describeColumn));
                if (delivered.Length > 0)
                {
                    item.Intent ??= MdText.Truncate(delivered, 4000);
                }
            }

            item.ParentRef ??= PhaseRefFor(reference);

            var verifyCell = MarkdownTable.Cell(row, verifyColumn);
            if (!MdText.IsEmpty(verifyCell))
            {
                // item.verify_cmd is a command an agent runs, so a cell that is a sentence about
                // verification contributes the command it quotes and is counted where it quotes none.
                if (ExtractCommand(verifyCell) is { } command)
                {
                    item.VerifyCmd ??= MdText.Truncate(command, 400);
                }
                else
                {
                    item.VerifyCmd ??= MdText.Truncate(MdText.Plain(verifyCell), 400);
                    run.Unmapped.Record(
                        "verify-cell-with-no-runnable-command",
                        "a plan Verify cell describing the check in prose",
                        MdText.Truncate(MdText.Plain(verifyCell), 120),
                        _currentFile);
                }
            }

            var criterion = MdText.Plain(MarkdownTable.Cell(row, criterionColumn));
            if (criterion.Length > 0 && !item.Criteria.Contains(criterion))
            {
                item.Criteria.Add(MdText.Truncate(criterion, 1000));
            }

            foreach (var tag in Vocabulary.ReadRiskTags(MarkdownTable.Cell(row, reviewColumn))
                         .Concat(Vocabulary.ReadRiskTags(titleCell)))
            {
                if (!item.RiskTags.Contains(tag))
                {
                    item.RiskTags.Add(tag);
                }
            }

            // A plan row states intent; a ledger row states outcome. Only the latter closes an item.
            if (Vocabulary.ReadVerdict(label) is { } verdict && verdict.Status == ItemStatus.Done)
            {
                ApplyVerdict(item, verdict);
            }
        }
    }

    private void ReadFindingTable(MarkdownTable table)
    {
        var refColumn = table.Column("#", "ref", "id");
        var findingColumn = table.Column("finding");
        var fixColumn = table.Column("fix");

        var ordinal = 0;
        foreach (var row in table.Rows)
        {
            ordinal++;
            var finding = MdText.Plain(MarkdownTable.Cell(row, findingColumn));
            if (finding.Length == 0)
            {
                continue;
            }

            var reference = Refs.Parse(MarkdownTable.Cell(row, refColumn)) ?? "F" + ordinal;
            var item = run.Item("finding-" + reference, "finding");
            item.Title = MdText.Truncate(finding, 200);
            item.Intent = MdText.Truncate(MdText.Plain(MarkdownTable.Cell(row, fixColumn)), 2000);
            item.Source = "review";
            item.ParentRef ??= _currentItemRef ?? _currentPhaseRef;
        }
    }

    private void ReadCommandResultTable(MarkdownTable table)
    {
        var commandColumn = table.Column("command");
        var resultColumn = table.Column("result");
        var item = CurrentItem();

        foreach (var row in table.Rows)
        {
            var command = MdText.Plain(MarkdownTable.Cell(row, commandColumn));
            var result = MdText.Plain(MarkdownTable.Cell(row, resultColumn));
            if (command.Length == 0 || result.Length == 0)
            {
                continue;
            }

            run.Verifications.Add(new VerificationImport(
                item?.Ref ?? "",
                MdText.Truncate(command, 400),
                MdText.Truncate(result, 800),
                Passed(result, null),
                VerifyRunner.Orchestrator,
                null));
        }
    }

    private void ReadRedCheckTable(MarkdownTable table)
    {
        var sabotageColumn = table.Column("sabotage");
        var failedColumn = table.Column("failed", "result");
        var whichColumn = table.Column("which", "tests");

        var variants = new List<object>();
        var failed = 0;

        foreach (var row in table.Rows)
        {
            var sabotage = MdText.Plain(MarkdownTable.Cell(row, sabotageColumn));
            if (sabotage.Length == 0)
            {
                continue;
            }

            var failedCell = MdText.Plain(MarkdownTable.Cell(row, failedColumn));
            var count = FirstInteger(failedCell);
            failed += count ?? 0;

            variants.Add(new
            {
                sabotage = MdText.Truncate(sabotage, 300),
                failed = count,
                which = MdText.Truncate(MdText.Plain(MarkdownTable.Cell(row, whichColumn)) is { Length: > 0 } w
                    ? w
                    : failedCell, 500),
            });
        }

        if (variants.Count == 0)
        {
            return;
        }

        var item = CurrentItem();
        if (item?.VerifyCmd is null)
        {
            run.Unmapped.Record(
                "red-check-without-verify-command",
                $"{variants.Count} sabotage variants under " + (item?.Ref ?? "no task"),
                table.Signature,
                _currentFile);
            return;
        }

        run.Verifications.Add(new VerificationImport(
            item.Ref,
            item.VerifyCmd,
            $"red-check: {variants.Count} variants, {failed} failing tests",
            failed > 0,
            VerifyRunner.Worker,
            JsonSerializer.Serialize(new { failed_count = failed, variants })));
    }

    private ItemImport? CurrentItem()
    {
        if (_currentItemRef is not null && run.Items.TryGetValue(_currentItemRef, out var item))
        {
            return item;
        }

        return _currentPhaseRef is not null && run.Items.TryGetValue(_currentPhaseRef, out var phase) ? phase : null;
    }

    private void AddVerification(ItemImport item, string? verifyCell, Verdict? verdict, VerifyRunner runBy)
    {
        var result = MdText.Plain(verifyCell);
        if (result.Length == 0 || MdText.IsEmpty(verifyCell))
        {
            return;
        }

        var command = ExtractCommand(verifyCell) ?? item.VerifyCmd;
        if (command is null)
        {
            run.Unmapped.Record(
                "verification-result-without-command",
                "a verify cell carrying a result line and no command",
                MdText.Truncate(result, 120),
                _currentFile);
            return;
        }

        run.Verifications.Add(new VerificationImport(
            item.Ref,
            MdText.Truncate(command, 400),
            MdText.Truncate(result, 800),
            Passed(result, verdict),
            runBy,
            null));
    }

    private static readonly string[] CommandHeads =
    [
        "dotnet ", "pnpm ", "npm ", "npx ", "psql ", "git ", "docker ", "node ", "python ",
        "pytest ", "bash ", "pwsh ", "powershell ", "curl ", "make ", "GET ", "POST ", "PUT ", "DELETE ",
    ];

    /// <summary>
    /// The command out of a verify cell, taken only from a backticked run that begins with a known
    /// executable. The corpus writes the result far more often than the command it came from, so
    /// guessing here would manufacture evidence.
    /// </summary>
    public static string? ExtractCommand(string? cell)
    {
        if (cell is null)
        {
            return null;
        }

        foreach (Match match in CodeSpanRegex().Matches(cell))
        {
            var candidate = match.Groups[1].Value.Trim();
            if (CommandHeads.Any(head => candidate.StartsWith(head, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool Passed(string result, Verdict? verdict)
    {
        var normalized = MdText.Normalize(result);

        if (result.Contains('❌') || normalized.Contains("0 passed", StringComparison.Ordinal))
        {
            return false;
        }

        if (result.Contains('✅') || normalized.Contains("0 failed", StringComparison.Ordinal))
        {
            return true;
        }

        if (FailedCountRegex().Match(normalized) is { Success: true } failure)
        {
            return failure.Groups[1].Value == "0";
        }

        return verdict is null || verdict.Status is ItemStatus.Done;
    }

    private static int? FirstInteger(string? text)
    {
        var match = IntegerRegex().Match(text ?? "");
        return match.Success && int.TryParse(match.Value, out var value) ? value : null;
    }

    // ------------------------------------------------------- heading sections

    private void ReadHeadingSection(MarkdownBlock block)
    {
        var heading = MdText.Normalize(block.Heading);
        if (heading.Length == 0)
        {
            if (!(_isPlan && ReadPlanChecklist(block, heading)))
            {
                CountStrayCheckboxes(block);
            }

            return;
        }

        if (IsFollowupHeading(heading))
        {
            ReadFollowups(block);
            return;
        }

        if (heading.Contains("task log", StringComparison.Ordinal))
        {
            ReadTaskLogList(block);
            return;
        }

        if (_isPlan && ReadPlanChecklist(block, heading))
        {
            return;
        }

        if (heading.Contains("carried forward", StringComparison.Ordinal)
            || heading.Contains("carry forward", StringComparison.Ordinal)
            || heading.Contains("carry into", StringComparison.Ordinal))
        {
            ReadNotes(block, NoteKind.CarryForward);
            return;
        }

        if (heading.Contains("do not re derive", StringComparison.Ordinal)
            || heading.Contains("do not rederive", StringComparison.Ordinal)
            || heading.Contains("do not rediscover", StringComparison.Ordinal))
        {
            ReadNotes(block, NoteKind.DoNotRederive);
            return;
        }

        if (heading.Contains("incident", StringComparison.Ordinal))
        {
            AddNote(NoteKind.Incident, MdText.Plain(block.Heading) + " — " + block.Body);
            return;
        }

        if (heading.Contains("correction", StringComparison.Ordinal))
        {
            AddNote(NoteKind.Correction, MdText.Plain(block.Heading) + " — " + block.Body);
            return;
        }

        if (heading.Contains("environment", StringComparison.Ordinal))
        {
            ReadNotes(block, NoteKind.Environment);
            return;
        }

        if (heading.Contains("decision", StringComparison.Ordinal)
            || heading.Contains("standing grant", StringComparison.Ordinal))
        {
            ReadNotes(block, NoteKind.Decision);
            return;
        }

        if (heading.Contains("orchestrator edit", StringComparison.Ordinal))
        {
            ReadOrchestratorEdits(block);
            return;
        }

        if (heading.Contains("re plan", StringComparison.Ordinal))
        {
            run.Events.Add(new EventImport(
                _currentPhaseRef,
                "replanned",
                ActorRole.Orchestrator,
                "orchestrator",
                MdText.Truncate(MdText.Plain(block.Heading), 200),
                JsonSerializer.Serialize(new { body = MdText.Truncate(block.Body, 4000), file = _currentFile })));
            return;
        }

        if (heading.Contains("exception", StringComparison.Ordinal))
        {
            run.Events.Add(new EventImport(
                _currentItemRef ?? _currentPhaseRef,
                "incident",
                ActorRole.Orchestrator,
                "orchestrator",
                MdText.Truncate(MdText.Plain(block.Heading), 200),
                JsonSerializer.Serialize(new { body = MdText.Truncate(block.Body, 4000), file = _currentFile })));
            return;
        }

        if (heading.Contains("assumption", StringComparison.Ordinal)
            && !MarkdownTable.FindAll(block.Body).Any(t => t.Has("assumption")))
        {
            var entries = Bullets(block.Body).Count();
            if (entries > 0)
            {
                run.Unmapped.Record(
                    "assumptions-as-a-list-not-a-table",
                    "an assumptions section with no evidence-level or reversal-cost column",
                    entries + " entries under " + MdText.Truncate(MdText.Plain(block.Heading), 60),
                    _currentFile);
                return;
            }
        }

        if (heading == "goal" && run.Goal.Length == 0)
        {
            run.Goal = block.Body.Trim();
            return;
        }

        // doc 02 §13: the run's live state is a query over the rows, so importing it as text would
        // recreate the second copy of status this project exists to delete.
        if (heading is "status" or "current status")
        {
            return;
        }

        CountStrayCheckboxes(block);
    }

    private static bool IsFollowupHeading(string heading)
        => heading.Contains("follow up", StringComparison.Ordinal)
           || heading.Contains("followup", StringComparison.Ordinal)
           || heading.Contains("deferred", StringComparison.Ordinal)
           || heading.Contains("still open", StringComparison.Ordinal)
           || heading.Contains("not done", StringComparison.Ordinal);

    /// <summary>
    /// A plan whose tasks are checkbox lines rather than table rows — the shape behind RECON-CORPUS §5's
    /// 424 unchecked boxes. Each line carries everything a task row needs:
    /// <c>- [ ] **1.1 Title** … Files: … · Done: … · Verify: dotnet test …</c>, and the box itself is the
    /// status. A checklist under a criteria heading is not a task list and becomes the criteria instead.
    /// </summary>
    private bool ReadPlanChecklist(MarkdownBlock block, string heading)
    {
        var boxes = Checkboxes(block.Body).ToList();
        if (boxes.Count == 0)
        {
            return false;
        }

        if (IsCriteriaHeading(heading))
        {
            return ReadCriteria(block, boxes);
        }

        foreach (var (text, isChecked) in boxes)
        {
            if (Refs.ParseLabel(text) is not { } parsed)
            {
                continue;
            }

            var (reference, label) = parsed;
            var item = run.Item(reference, "task");

            if (item.Title.Length == 0)
            {
                item.Title = MdText.Truncate(Headline(label.Length > 0 ? label : text), 200);
            }

            item.ParentRef ??= PhaseRefFor(reference);
            item.Intent ??= MdText.Truncate(text, 4000);

            if (ClauseRegex().Matches(text) is { Count: > 0 } clauses)
            {
                foreach (var clause in clauses.Cast<Match>())
                {
                    var name = MdText.Normalize(clause.Groups["name"].Value);
                    var value = clause.Groups["value"].Value.Trim(' ', '·', '.', ';');
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    if (name.StartsWith("done", StringComparison.Ordinal) && !item.Criteria.Contains(value))
                    {
                        item.Criteria.Add(MdText.Truncate(value, 1000));
                    }
                    else if (name.StartsWith("verify", StringComparison.Ordinal))
                    {
                        item.VerifyCmd ??= MdText.Truncate(ExtractCommand(value) ?? value, 400);
                    }
                }
            }

            foreach (var tag in Vocabulary.ReadRiskTags(text).Where(t => !item.RiskTags.Contains(t)))
            {
                item.RiskTags.Add(tag);
            }

            if (!isChecked)
            {
                continue;
            }

            item.Status = ItemStatus.Done;
            item.Resolution = ItemResolution.Accepted;
        }

        return true;
    }

    private static bool IsCriteriaHeading(string heading)
        => heading.Contains("done when", StringComparison.Ordinal)
           || heading.Contains("criteri", StringComparison.Ordinal)
           || heading.Contains("acceptance", StringComparison.Ordinal);

    private bool ReadCriteria(MarkdownBlock block, IReadOnlyList<(string Text, bool Checked)> boxes)
    {
        if (CurrentItem() is not { } item)
        {
            run.Unmapped.Record(
                "criteria-checklist-with-no-task-in-scope",
                "an acceptance checklist under no phase and no task",
                MdText.Truncate(MdText.Plain(block.Heading), 80),
                _currentFile);
            return true;
        }

        foreach (var (text, _) in boxes.Where(b => !item.Criteria.Contains(b.Text)))
        {
            item.Criteria.Add(MdText.Truncate(text, 1000));
        }

        return true;
    }

    private void ReadFollowups(MarkdownBlock block)
    {
        var ordinal = 0;
        foreach (var (text, isChecked) in Checkboxes(block.Body))
        {
            ordinal++;
            var reference = "fu-" + Refs.Slug(text, 40) + "-" + ordinal;
            var item = run.Item(reference, "followup");
            item.Title = MdText.Truncate(text, 200);
            item.Intent = MdText.Truncate(text, 4000);
            item.ParentRef ??= _currentPhaseRef;

            if (isChecked)
            {
                item.Status = ItemStatus.Done;
                item.Resolution = ItemResolution.Accepted;
            }
        }

        if (ordinal > 0)
        {
            return;
        }

        foreach (var bullet in Bullets(block.Body))
        {
            ordinal++;
            var reference = "fu-" + Refs.Slug(bullet, 40) + "-" + ordinal;
            var item = run.Item(reference, "followup");
            item.Title = MdText.Truncate(bullet, 200);
            item.Intent = MdText.Truncate(bullet, 4000);
            item.ParentRef ??= _currentPhaseRef;
        }
    }

    /// <summary>
    /// A task log written as a list rather than a table — <c>- **3.1 config doc** — ACCEPTED. …</c>.
    /// RECON-CORPUS §2 counts 24 `Task log` headings and only 7 canonical task-log tables, so the list
    /// form carries a large minority of every verdict the corpus ever recorded.
    /// </summary>
    private void ReadTaskLogList(MarkdownBlock block)
    {
        foreach (var raw in RawBullets(block.Body))
        {
            var bullet = MdText.Plain(raw);
            var match = TaskHeadingRegex().Match(bullet);
            if (!match.Success)
            {
                run.Unmapped.Record(
                    "task-log-entry-without-a-task-ref",
                    "a task-log line whose first token is not a task handle",
                    MdText.Truncate(bullet, 120),
                    _currentFile);
                continue;
            }

            var reference = match.Groups[1].Value;
            var rest = Headline(match.Groups[2].Value.Trim());
            var verdict = Vocabulary.ReadShoutedVerdict(bullet);

            var item = run.Item(reference, "task");
            if (item.Title.Length == 0)
            {
                item.Title = MdText.Truncate(rest.Length > 0 ? rest : reference, 200);
            }

            item.ParentRef ??= PhaseRefFor(reference);
            item.Intent ??= MdText.Truncate(bullet, 4000);
            _currentItemRef = reference;

            if (verdict is not null)
            {
                ApplyVerdict(item, verdict);
                run.Events.Add(new EventImport(
                    reference,
                    "judged",
                    ActorRole.Orchestrator,
                    "orchestrator",
                    $"{reference}: {verdict.Token}",
                    JsonSerializer.Serialize(new { verdict = verdict.Token, source = "task-log-list", file = _currentFile })));
            }
            else
            {
                run.Unmapped.Record(
                    "task-log-entry-without-a-verdict",
                    "a task-log line naming a task and no outcome token",
                    MdText.Truncate(bullet, 120),
                    _currentFile);
            }

            if (MdText.CommitSha(raw) is { } sha)
            {
                run.Artifacts.Add(new ArtifactImport(reference, "commit", sha, "{}"));
            }
        }
    }

    /// <summary>The leading clause of a log line: what the task was, before the sentence about it.</summary>
    private static string Headline(string text)
    {
        var cut = text.IndexOfAny(['—', '–']);
        var head = cut > 0 ? text[..cut] : text;
        return head.Trim().Length > 0 ? head.Trim() : text.Trim();
    }

    private void ReadNotes(MarkdownBlock block, NoteKind kind)
    {
        var bullets = Bullets(block.Body).ToList();
        if (bullets.Count == 0)
        {
            AddNote(kind, block.Body);
            return;
        }

        foreach (var bullet in bullets)
        {
            AddNote(kind, bullet);
        }
    }

    private void ReadOrchestratorEdits(MarkdownBlock block)
    {
        var bullets = Bullets(block.Body).ToList();
        if (bullets.Count == 0 || bullets.All(b => MdText.Normalize(b) is "none" or "none " or ""))
        {
            return;
        }

        foreach (var bullet in bullets)
        {
            run.Events.Add(new EventImport(
                _currentPhaseRef,
                "orchestrator_edit",
                ActorRole.Orchestrator,
                "orchestrator",
                MdText.Truncate(bullet, 200),
                JsonSerializer.Serialize(new { body = MdText.Truncate(bullet, 4000), file = _currentFile })));
        }
    }

    private void AddNote(NoteKind kind, string body)
    {
        var text = body.Trim();
        if (text.Length == 0)
        {
            return;
        }

        run.Notes.Add(new NoteImport(kind, MdText.Truncate(text, 8000), _currentItemRef ?? _currentPhaseRef));
    }

    /// <summary>
    /// RECON-CORPUS §5: 84.5% of every checkbox in the corpus is unchecked. The ones outside a
    /// follow-up section have no lifecycle anywhere in this schema, so they are counted, not invented.
    /// </summary>
    private void CountStrayCheckboxes(MarkdownBlock block)
    {
        var boxes = Checkboxes(block.Body).ToList();
        if (boxes.Count == 0)
        {
            return;
        }

        run.Unmapped.Record(
            "checkbox-outside-a-follow-up-section",
            boxes.Count(b => !b.Checked) + " unchecked, " + boxes.Count(b => b.Checked) + " checked",
            MdText.Truncate(block.Heading ?? boxes[0].Text, 80),
            _currentFile);
    }

    // ------------------------------------------------------------ conventions

    private void ReadConventionsSection(MarkdownBlock block)
    {
        if (block.Heading is null)
        {
            return;
        }

        var heading = MdText.Normalize(block.Heading);
        var kind = heading.Contains("red line", StringComparison.Ordinal)
                   || heading.Contains("safety", StringComparison.Ordinal)
                   || heading.Contains("never", StringComparison.Ordinal)
            ? ConstraintKind.RedLine
            : heading.Contains("command", StringComparison.Ordinal)
              || heading.Contains("build", StringComparison.Ordinal)
              || heading.Contains("test", StringComparison.Ordinal)
              || heading.Contains("verif", StringComparison.Ordinal)
                ? ConstraintKind.Command
                : heading.Contains("stack", StringComparison.Ordinal)
                  || heading.Contains("fact", StringComparison.Ordinal)
                  || heading.Contains("environment", StringComparison.Ordinal)
                  || heading.Contains("architecture", StringComparison.Ordinal)
                    ? ConstraintKind.StackFact
                    : heading.Contains("generated", StringComparison.Ordinal)
                      || heading.Contains("scaffold", StringComparison.Ordinal)
                        ? ConstraintKind.GeneratedCode
                        : ConstraintKind.DomainRule;

        var rules = Bullets(block.Body).ToList();
        if (rules.Count == 0)
        {
            var body = block.Body.Trim();
            if (body.Length == 0)
            {
                return;
            }

            if (body.Length > 1200)
            {
                run.Unmapped.Record(
                    "conventions-prose-section",
                    "a CONVENTIONS section with no list to split on",
                    MdText.Truncate(MdText.Plain(block.Heading), 80),
                    _currentFile);
                return;
            }

            rules.Add(body);
        }

        foreach (var rule in rules)
        {
            run.Constraints.Add(new ConstraintImport(
                kind,
                MdText.Truncate(rule, 4000),
                Route(rule),
                Vocabulary.ReadRiskTags(rule)));
        }
    }

    /// <summary>
    /// CONVENTIONS §9: a prohibition with no route is itself a defect, so the route is extracted where
    /// the text carries one rather than left for a reader to infer.
    /// </summary>
    private static string? Route(string rule)
    {
        var match = RouteRegex().Match(rule);
        return match.Success ? MdText.Truncate(MdText.Plain(match.Value), 400) : null;
    }

    // ----------------------------------------------------------------- briefs

    private void ReadBrief(string relativePath, DocImport doc)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var reference = Refs.Parse(name["brief-".Length..]) ?? Refs.Slug(name, 40);

        var item = run.Item(reference, "task");
        if (item.Title.Length == 0)
        {
            item.Title = MdText.Truncate(doc.Title, 200);
        }

        item.ParentRef ??= PhaseRefFor(reference);
        run.Attempts.Add(new AttemptImport(reference, doc.Slug, null));
    }

    // ------------------------------------------------------------- primitives

    public static IEnumerable<(string Text, bool Checked)> Checkboxes(string body)
    {
        foreach (var entry in ListEntries(body))
        {
            var match = CheckboxRegex().Match(entry);
            if (match.Success)
            {
                yield return (MdText.Plain(entry[match.Length..]), match.Groups[1].Value is "x" or "X");
            }
        }
    }

    public static IEnumerable<string> Bullets(string body)
    {
        foreach (var raw in RawBullets(body))
        {
            var text = MdText.Plain(raw);
            if (text.Length > 0)
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// The same entries with their markdown intact. A commit sha is only trusted inside backticks
    /// (<see cref="MdText.CommitSha"/>), so a reader that needs one cannot take the plain text.
    /// </summary>
    public static IEnumerable<string> RawBullets(string body)
    {
        foreach (var entry in ListEntries(body))
        {
            if (CheckboxRegex().IsMatch(entry))
            {
                continue;
            }

            var raw = BulletRegex().Replace(entry, "", 1);
            if (raw.Trim().Length > 0)
            {
                yield return raw;
            }
        }
    }

    /// <summary>
    /// Top-level list entries with their continuation lines folded in. The corpus indents four, six and
    /// eight spaces under one bullet, so a line-per-entry reader would shred every long follow-up.
    /// </summary>
    private static IEnumerable<string> ListEntries(string body)
    {
        var current = new List<string>();
        var fenced = false;

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            var indent = line.Length - trimmed.Length;

            if (indent <= 3 && BulletRegex().IsMatch(trimmed))
            {
                if (current.Count > 0)
                {
                    yield return string.Join(' ', current);
                }

                current = [trimmed];
                continue;
            }

            if (current.Count == 0)
            {
                continue;
            }

            if (trimmed.Length == 0)
            {
                yield return string.Join(' ', current);
                current = [];
                continue;
            }

            current.Add(trimmed);
        }

        if (current.Count > 0)
        {
            yield return string.Join(' ', current);
        }
    }

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+\[([ xX])\]\s*")]
    private static partial Regex CheckboxRegex();

    [GeneratedRegex(@"^#*\s*(?:⚠\s*)?Phase\s+([0-9]+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseHeadingRegex();

    [GeneratedRegex(@"^([0-9]+\.[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)\s*[—–-]?\s*(.*)$")]
    private static partial Regex TaskHeadingRegex();

    [GeneratedRegex(@"^[A-Za-z]{0,3}-?[0-9][0-9A-Za-z.\-]*$")]
    private static partial Regex AssumptionHandleRegex();

    /// <summary>A plan line's trailing clauses: <c>Files: … · Done: … · Verify: …</c>.</summary>
    [GeneratedRegex(@"\b(?<name>Files|Done(?: when)?|Verify|Review|Risk)\s*:\s*(?<value>[^·]*)")]
    private static partial Regex ClauseRegex();

    [GeneratedRegex(@"^brief[-.]", RegexOptions.IgnoreCase)]
    private static partial Regex BriefRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeSpanRegex();

    [GeneratedRegex(@"\bfailed[: ]+([0-9]+)")]
    private static partial Regex FailedCountRegex();

    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"\b(CONFIRMED|RESOLVED|SETTLED|CLOSED|FALSIFIED|DISPROVEN|RETRACTED|OBSOLETE|MOOT)\b")]
    private static partial Regex ResolvedRegex();

    [GeneratedRegex(@"\b(FALSIFIED|DISPROVEN|RETRACTED|WRONG|FALSE)\b")]
    private static partial Regex FalsifiedRegex();

    [GeneratedRegex(@"\b(OBSOLETE|MOOT|NO LONGER)\b")]
    private static partial Regex ObsoleteRegex();

    [GeneratedRegex(@"(?:instead[^.]*\.|use [^.]*\.|reach [^.]*\.|narrow with[^.]*\.)", RegexOptions.IgnoreCase)]
    private static partial Regex RouteRegex();
}
