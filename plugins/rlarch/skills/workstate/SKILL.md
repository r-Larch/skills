---
name: workstate
description: "Plan, propose and triage work in the WorkState store (the `work_*` MCP tools) without executing any of it. Turns a request like \"plan feature XY\" into a run in WorkState: a goal, units or phases with done-criteria and a verification command each, binding rules, open assumptions and ordering. Answers \"what should we build next\" by reading the board and the repo and ranking candidates. Grooms a backlog by proposing what to close, defer, merge or reorder. Then it stops at the approval gate, and the human hands the plan to an executor: \"approved, execute as /rlarch:controller\" or \"/rlarch:orchestrate\". It writes no product code, launches no agent and starts nothing. USE FOR: planning a feature, fix or migration before anyone builds it; \"propose what we should build next\"; triaging or grooming a backlog; \"where are we\" or \"show me the workstate\"; revising a plan after feedback. DO NOT USE FOR: executing a plan (use /rlarch:controller or /rlarch:orchestrate), importing existing PLAN.md or TODO files (use /rlarch:workstate-import), or installing the server (use /rlarch:workstate-setup)."
---

# WorkState: plan, propose, triage

You are the **planner**, not the builder. Your output is records in WorkState and a short proposal
in chat. Nothing in the repo changes: no product code, no branch, no worktree, no agent. Execution
starts only when the human says so, in words, and it starts in an executor skill:

> *"approved, execute as /rlarch:controller"*: many mostly independent units, one nested session each.
> *"approved, execute as /rlarch:orchestrate"*: one plan, run in that session with subagents.

The plan lives in the store so that handoff costs nothing. The executor calls `work_brief`, finds the
run you made, and starts from it. Anything you leave only in chat is lost at that handoff.

---

## 0. Preflight

Call **`work_brief(repo: {remote, root})`**. `remote` is `git remote get-url origin` (omit it when
there is no origin). `root` is the parent directory of
`git rev-parse --path-format=absolute --git-common-dir`.

- **No `work_*` tools** → *"/rlarch:workstate needs the WorkState MCP server. Run
  `/rlarch:workstate-setup`."* Stop. There is no markdown fallback.
- **`work_create` is missing** → the session has a worker token, and workers cannot plan. Say so in
  one line and stop.
- **`session  unnamed`** → `work_session(name:)` with the name `ListAgents` prints first.
- **The repo has no project** → do not create one yet. Creating a project is part of the plan you
  propose (§2, step 4).

Refs come from the server. Never invent one, in a call or in chat.

---

## 1. Which mode? Decide from the request

| The human says | Mode | Writes to the store |
|---|---|---|
| "plan feature XY", "plan the fix for …", "break this spec down" | **Plan** §2 | yes: a run, then stop |
| "propose what we should build next", "what's worth doing" | **Propose** §3 | no, until a candidate is picked |
| "triage", "groom the backlog", "clean up the board" | **Triage** §4 | no, until the moves are approved |
| "where are we", "show me the workstate" | **Status** §5 | no |
| feedback on a plan you made ("split 3", "drop the API part") | **Revise** §6 | yes |

When a request mixes modes ("propose something and plan it"), do them in order and still stop at
each gate.

---

## 2. Plan: from a request to an approvable run

1. **Write the goal as one paragraph**: what is true when this is done, and how a human confirms it.
   If you cannot write it, ask one question. Do not guess at scope.
2. **Read to plan, not to learn.** Check that the feature does not already exist, find the files it
   touches, the tests that cover them and the command that proves it, and anything already open in
   the store (`work_search`) that overlaps. For more than a few files, use read-only `Explore`
   agents: up to 4 in parallel, one disjoint question each, short answers with paths. If the work
   targets a system you do not control, a read-only probe beats any assumption about it.
3. **Pick the executor, because it decides the plan's shape.**
   - **Controller** when the work splits into units that are each big enough for their own context
     window and mostly independent. Shape: one `run` (the queue), one `task` per unit.
   - **Orchestrate** when it is one connected plan. Shape: one `run`, then `phase`s, then `task`s, as
     `/rlarch:orchestrate` `references/decompose.md` describes.
   - **No executor** when it is one small task. Say so and offer to do it directly. A run for a
     ten-line change is overhead.
4. **Write it**, one `work_create` per level, top-down, reading back each call's refs:
   - the **project**, only when the repo is unbound: `kind: "project"` with `repo: {remote, root}`.
     When it belongs to a project that already exists, `work_update(ref, repo:)` binds it instead.
     Never create a second project for one repo.
   - the **run**: `parent:` the project, `ref:` a short slug, `intent:` the goal paragraph plus
     *"planned by /rlarch:workstate, awaiting approval; executor: controller|orchestrate"*.
   - its **tasks** (and phases): each with `criteria` that state outcomes, not activities, and a
     `verify_cmd` that would fail if the work were wrong. `work_create` refuses a task without both.
     Put *where it is specified* in `intent`: file and section, never a restatement.
   - **binding rules** with `work_constrain(scope: "<run>")`: what must not change, the verification
     commands, stack facts, hazards. A `red_line` needs a `route:`.
   - **assumptions** with `work_assume`: everything you believe but did not check.
   - **order** with `work_link(type: "blocks")` for every real dependency, and `priority` for the
     rest. Put the diagnostic before the diagnosis.
   - **open decisions** as `kind: "question"` items under the run. An executor handed an undecided
     question will decide it, quietly. A unit that waits on a question is `blocked` until it is
     answered.
5. **Present it, then stop.** Show `work_tree(scope: "<run>")` and add:
   - the goal paragraph,
   - the executor you recommend and why,
   - the open questions and assumptions the human should look at,
   - the dashboard link (the `web` line `work_brief`/`work_get` print for the run),
   - the literal handoff line: *"approved, execute as /rlarch:controller"* (or orchestrate).

   Do not start anything. Do not ask "shall I start?" either. The handoff line is how the human
   approves.

**Size check before presenting.** A unit with more than five criteria, or one that needs words like
"and also", is two units. A plan with one task is not a plan (step 3).

---

## 3. Propose: "what should we build next"

Read, in this order, and stop reading once the ranking would not change:

1. `work_brief`, then `work_tree` on the project: open runs, open `followup`s, `finding`s, `question`s
   and `blocked` items, and the `attention` block (expired claims, assumptions past their revisit
   point).
2. `work_search` for what the human's words point at, if they named an area.
3. The repo, lightly: `git log --oneline -20`, failing or skipped tests the human mentioned, and
   `TODO`/`FIXME` density in the area under discussion. Do not audit the whole codebase.

Then propose **3 to 5 candidates**, ranked. For each, give:

- **what** it is, and its ref if it is already in the store,
- **why now**: what it unblocks, what it costs to leave it, or who asked for it,
- **size** (one task / a few units / a run) and the executor it would need,
- **risk or open decision**, if any.

Rank by what unblocks the most, not by what is most interesting. Say plainly when the best next
thing is to close or answer something rather than build something.

Write nothing. When the human picks one, run §2 on it. The pick is the approval to *plan*, not to
execute.

---

## 4. Triage: groom the board

Sweep the project for items that no longer tell the truth:

| Finding | Proposed move |
|---|---|
| done in the code, still open in the store | close with evidence: an executor's job, so name the verify command |
| no longer wanted | `work_status(ref, "cancelled", reason:)` |
| wanted, not now | `work_status(ref, "deferred", target:, reason:)`. Deferring moves the item somewhere real |
| two items, one piece of work | a `duplicates` or `supersedes` link |
| blocked on a question nobody owns | name the decision and whose it is |
| assumption past its revisit point | check it or re-date it |
| claim expired | name it; whoever held it decides |
| order wrong | new `blocks` links or a `priority` change |

Present the moves as one table with a reason per row, then apply only the rows the human approves.
Never close an item as done yourself. Done needs a verification someone ran, and that is the
executor's job.

---

## 5. Status: "where are we", "show me the workstate"

Answer from `work_brief` (and `work_tree` if they asked about a run) in outcomes: what landed, what
is in flight and who holds it, what is blocked and on whom, what needs the human. Give the `web`
link from the brief so the human can open the dashboard; there is no tool that opens a browser.
Keep it to a screen.

---

## 6. Revise

Feedback on a plan is an edit to the store, not a new plan. Use `work_update` for wording, criteria
and verify commands, `work_create` for new units, `work_status(…, "cancelled")` for dropped ones,
and new links for reordering. Record the reason with
`work_note(kind: "decision", from: "<run>", body:)`. Then show `work_tree` again and stop at the
same gate.

---

## 7. Handoff: what the executor does with your run

When the human answers *"approved, execute as /rlarch:controller"* (or orchestrate), the executor
skill loads in the same session or a new one, and:

1. `work_brief` finds the run you created. The human's message names it, or it is the run awaiting
   approval in this repo.
2. It records the approval: `work_note(kind: "decision", from: "<run>", body: "plan approved by
   <human>: execute as <skill>")`.
3. It **skips its own planning and approval gate**. The approval it would ask for has been given.
   It still runs its preflight and its plan audit (orchestrate) or classification (controller) on
   your records, and raises anything wrong as a finding before the first agent starts.

So everything the executor needs must be in the store when you stop: the goal in the run's intent,
the rules as constraints, the decisions as questions or notes. If you know something the executor
needs and it is not in a record, write it down now.

---

## Rules

- **No execution.** No product code, no commits, no branches, no worktrees, no `agent_launch`, no
  subagent that writes. Read-only recon only.
- **Approval to plan is not approval to build.** Only the handoff line starts execution.
- **One plan, one run.** Do not scatter a feature across existing runs. Link to them instead.
- **Don't duplicate the store.** Search before you create. An overlapping item gets a link, not a
  twin.
- **Never mark anything done.** Closing is guarded, and the guard is the point.
