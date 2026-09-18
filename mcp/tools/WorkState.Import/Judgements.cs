namespace WorkState.Import;

/// <summary>
/// One line per unmapped category, written once here rather than re-argued in the report. The question
/// each answers is the only one the controller needs: does the schema grow a column, or is this prose
/// that belongs in a block?
/// </summary>
public static class Judgements
{
    public static readonly IReadOnlyDictionary<string, string> ByCategory = new Dictionary<string, string>
    {
        ["checkbox-outside-a-follow-up-section"] =
            "Genuinely prose today, but the volume is the finding: these are plan and gate checklists "
            + "with no owner and no closing transition. `item_criterion` is the right home for the ones "
            + "attached to a task; the rest are a follow-up section that was never opened.",

        ["table"] =
            "Mostly prose in a grid — baselines, grants, environment tables. A block holds them "
            + "correctly. The exception is any header carrying a lifecycle word, which is a shape the "
            + "importer should learn rather than a column the schema should grow.",

        ["verification-result-without-command"] =
            "The schema should change. `verification.command` is NOT NULL, but the corpus records the "
            + "result line far more often than the command that produced it, and an orchestrator's "
            + "re-verification is usually prose with a count and no command at all.",

        ["verification-table-without-a-command-column"] =
            "The schema should change, and this is the same finding as the verify cells with no command: a "
            + "table of `| checked | result |` or `| step | result |` is evidence somebody gathered, with "
            + "no command anywhere in it. Add these counts to that one.",

        ["assumption-reversal-unreadable"] =
            "The schema should change. `reversal` is NOT NULL, and a resolved assumption's reversal "
            + "cost is written as `—` because it has stopped mattering. Either the column is nullable "
            + "or `assumption_state` closing has to clear it.",

        ["assumption-evidence-token"] =
            "Prose. `evidence_level` covers the ground; the corpus simply writes the provenance "
            + "('owner statement', 'recon, repo-wide grep') next to the level rather than as it. A "
            + "free-text `evidence_note` beside the enum would keep it.",

        ["assumptions-as-a-list-not-a-table"] =
            "Prose, and a process finding: these sections predate the table shape and carry no evidence "
            + "level or reversal cost at all, so importing them typed would manufacture both.",

        ["red-check-without-verify-command"] =
            "The schema should change, in the same direction as the verification finding: a red-check "
            + "is evidence in its own right and currently cannot be stored without a command to hang it on.",

        ["malformed-row"] =
            "Prose. A row whose cell count disagrees with its header is a hand-editing accident; the "
            + "block keeps it verbatim and the count says how often it happens.",

        ["conventions-prose-section"] =
            "Prose. A CONVENTIONS section with no list is an essay, and splitting it into rules would "
            + "invent boundaries its author did not write.",

        ["table-row-detached-from-its-header"] =
            "Recovered, not lost — but it is the clearest evidence for the store: a markdown table that "
            + "an author appends to across five hundred lines of commentary has no shape a reader can "
            + "rely on, and every one of these rows is a row the file format nearly threw away.",

        ["pipe-row-belonging-to-no-table"] =
            "Prose that happens to start with a pipe, or a row whose header was deleted. The block keeps it.",

        ["control-character-postgres-cannot-store"] =
            "Neither, and it is the one shape that could stop a run: a NUL byte in a ledger ends the "
            + "whole import with `22021: invalid byte sequence for encoding \"UTF8\": 0x00`. The store is "
            + "right to refuse it; the file is what is wrong. Counted, removed, and the file named.",

        ["verify-cell-with-no-runnable-command"] =
            "The schema is fine and the corpus is the problem: `item.verify_cmd` wants a command, and a "
            + "Verify cell that reads 'green, having first been red' cannot be run by anyone. The gap is "
            + "worth surfacing as a plan-quality check rather than a column.",

        ["task-log-entry-without-a-verdict"] =
            "Prose, but the count is the finding: a task log written as a list records the outcome inside "
            + "a sentence, so a run whose header says COMPLETE has tasks with no recorded closure at all.",

        ["task-log-entry-without-a-task-ref"] =
            "Prose. A log line like `**Spike** — the finding above` names no task, and inventing a ref "
            + "for it would put a row in the tree that no later file can address.",

        ["verdict-token"] =
            "Prose. Every token RECON-CORPUS §4 counted has a home; what lands here is a cell that "
            + "carries a date or a sentence where a verdict was expected.",
    };

    /// <summary>
    /// What the real corpus says about the model, as opposed to what it says about the parser. This is the
    /// section the controller reads first and the reason U2 runs before U3 (brief §3). It lives here rather
    /// than in the markdown because the report is regenerated on every run and hand-written prose in it
    /// would be lost — which is the exact failure the store exists to remove.
    /// </summary>
    public const string SchemaDoubts = """
        ## What the real data says about the model

        Six things, worst first. None of them was fixed here: a schema edited mid-import by the importer is
        a schema nobody reviewed.

        ### 1. `verification.command` is NOT NULL and the corpus usually has no command

        The single largest unmapped shape. A ledger records *the result* — `35/35, build 0 errors`,
        `full suite 1807/1` — far more often than the command that produced it, and a controller's
        re-verification is frequently a sentence with a count in it. Every one of those is evidence the
        store currently cannot hold, so the import writes a fraction of the verifications the corpus
        actually contains. Either `command` becomes nullable, or `verification` gains a shape for
        "verified, this is what I saw" with the command optional. This one blocks U3: a `verify` tool that
        demands a command will be unable to record most of what really happens.

        ### 2. `assumption.reversal` is NOT NULL and a closed assumption has no reversal cost

        The corpus writes `—` in the reversal column of every assumption it has already resolved, because
        the cost of reversing a settled question is not a fact about anything. The importer has to invent
        `medium` to satisfy the column. Make it nullable, or have the closing transition clear it.

        ### 3. `evidence_level` is an enum and the corpus writes the provenance, not the level

        `MEDIUM — matches how the real trial flow behaves`, `confirmed: the key is in configuration`,
        `raised by the 2.1 worker`. The level is recoverable from the leading clause; the *provenance* is
        the part a later reader needs and there is nowhere to put it. A free-text `evidence_note` beside
        the enum would keep it, and would cost one column.

        ### 4. `item.seq` is per-sibling, so `ready_frontier` cannot order across phases

        `SeqAllocator` numbers children within a parent, correctly — but the frontier query orders a whole
        run's subtree by `priority DESC, seq`, and every phase's first task has `seq = 1`. Over a real
        multi-phase run the frontier's order between phases is arbitrary. Either the frontier orders by
        the ancestor path as well, or "what next" needs the phase's own frontier and a phase order above
        it. Cheap to fix in SQL; expensive to discover in U3.

        ### 5. Nothing in the tree expresses "this checkbox belongs to that task"

        424 unchecked boxes across the corpus (RECON-CORPUS §5). In a plan they are tasks and this importer
        now reads them as tasks. In a ledger they are gate checklists, gate criteria and gate findings
        attached to a phase whose row already says PASSED — an inconsistency the store makes visible for
        the first time, and one `item_criterion` covers only where a task is in scope.

        ### 6. A run directory is assumed to hold one ledger, and the ref namespace assumes it too

        `UNIQUE (ref_scope_id, ref)` scopes a ref to the run. That is right, but two ledgers in one run
        directory (a re-planned run, an archived copy) both write tasks `1.1`, `1.2`, `1.3` and the second
        silently merges into the first. The corpus does not do this today — every run directory carries one
        `LEDGER.md` — so this is a latent hazard rather than a live defect. Worth a unique constraint or a
        documented rule before the MCP surface starts writing refs from more than one file.
        """;

    public const string Usage = """
        WorkState.Import — orchestration markdown into the WorkState store.

          WorkState.Import --root <path> [--root <path>…] [--connection <cs>] [--workspace <slug>]
                           [--report <file>] [--migrate] [--ephemeral]

        <path> is a .claude/orchestration/<slug> directory, a repo root, or a directory of repo roots.
        The connection string defaults to $WORKSTATE_CONNECTION, then to the compose.dev.yml database.

          --migrate    apply the migrations to that database first
          --ephemeral  ignore the connection string and import into a throwaway postgres:17 container,
                       which is how a measurement run reaches no database that holds real work
        """;
}
