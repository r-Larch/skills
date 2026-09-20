namespace WorkState.Mcp;

/// <summary>
/// <c>docs/workstate/05-server-instructions.md</c>, verbatim. Measured (PROBE Q0) to reach the model
/// in full, once per registered server, on every turn — the most expensive 40 lines in the product
/// and the only channel that works before the agent has called anything. Edit the document, not this
/// string, and re-measure the line count: <see cref="LineBudget"/> is asserted by a test.
/// </summary>
public static class WorkStateServerInstructions
{
    public const int LineBudget = 40;

    public const string Text =
        """
        WorkState is the work tracker for this session. It holds projects, runs, phases, tasks,
        findings, follow-ups and assumptions, with their dependencies, evidence and history. It
        replaces PLAN.md / LEDGER.md files: do not write those, write here.

        THE LOOP — everything else is a detour off this spine:
          work_brief  ->  work_next  ->  work_status(in_progress)  ->  do the work
                      ->  work_verify  ->  work_status(done)  ->  work_next

        START EVERY SESSION WITH work_brief. It is the recovery payload: goal, where you are, the
        literal next action, the constraints that bind you, open assumptions, and notes addressed to
        you. It is generated, so it cannot be stale. You do not need to read anything else first.
        On an empty server it tells you how to create your first project.

        CLOSING IS GUARDED, AND THE REFUSAL TELLS YOU WHAT IS MISSING:
          work_status(ref, "done") is refused unless every done-criterion is checked, a verification
          exists that YOU ran (not one a worker reported), no child is still open, and no blocker
          finding targets it. Read the refusal — it names the exact calls that fix it.
          You cannot drop work silently: deferring is a MOVE, not a status. work_status(ref,
          "deferred", target: ..., reason: ...) creates a real open item somewhere else.

        READ THE `attention` BLOCK at the end of every result. It is the only part that is about work
        you are NOT currently doing: expired claims, assumptions past their revisit point, notes
        addressed to an item you are about to brief. Acting on it costs one call; ignoring it is how
        a run ends with 84% of its checkboxes still open.

        REFS are short and scoped to your run — `2.3`, `3.R1`, `F-07`. From another run, qualify it:
        `entity-store/2.3`. Never invent a ref; get it from work_next, work_tree or work_search.

        YOUR ROLE limits which tools you can see, so a tool you cannot find is usually one your role
        does not have rather than one that does not exist. Workers report and verify; orchestrators
        judge, close and commit. If you need something you cannot see, say so — do not work around it.

        DO NOT: create a task without a verification command that could actually fail; record a status
        you did not verify yourself; re-read a document work_brief already gave you; or paste code or
        diffs into a note — store the commit sha and the file path instead.
        """;
}
