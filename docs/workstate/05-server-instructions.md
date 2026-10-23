# 05 — `ServerInstructions`: the exact text

> **Measured ([`PROBE.md`](PROBE.md) Q0):** this text reaches the model in full, under
> `# MCP Server Instructions` / `## workstate`, untruncated, **once per registered server**.
> It is in context for **every turn of every session**. It is the most expensive 40 lines in the
> product and the only channel that works before the agent has called anything.

## The budget

**≤ 40 lines.** It carries only what a tool description cannot: the **loop**, the **contract**, and
the **role**. It must never restate the tool list or an argument schema — `tools/list` already
carries those, and a duplicated schema is the thing that rots first.

Two rules, both learned the hard way in this repo:

- **Every prohibition ships with its route.** A rule demanding a capability the agent lacks is a trap
  the author set for themselves (`nested-claude`). "Don't close without evidence" is useless; "closing
  needs `work_verify` first" is a route.
- **State the role.** Because `[Authorize]` *hides* a tool rather than refusing it (measured), a
  worker gets no error explaining what it may not do — the tool simply is not there. Without a line
  saying so, it hunts for something that does not exist from where it stands.

## The text

```text
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
```

**Measured: 35 lines, 28 non-blank, ~580 tokens, widest line 94 chars.** Inside the 40-line
budget with room to grow — and re-measured on every change, because a budget nobody measures is a
wish.

## Why each block is there

| Block | Earns its place because |
|---|---|
| What this is + *"do not write those files"* | Without it the agent writes `LEDGER.md` **and** uses the tools, and the two disagree — the exact failure mode being replaced. |
| The loop | Seven tools in one line. Turns a flat list into a workflow, which no single tool description can do. |
| Start with `work_brief` | The single highest-value habit. Makes cold start and post-compaction recovery the same action. |
| The closure guard | The forced-closure contract, stated **before** the agent hits it, plus the promise that the refusal is actionable — so a refusal reads as guidance rather than as a wall. |
| Deferral is a move | The one rule an agent would otherwise route around by inventing a "deferred" status. |
| The attention block | It is the nag channel; if it is not read, it does not exist. Cites the measured 84% figure because a number persuades where an adjective does not. |
| Refs | Prevents invented refs, which is the most likely wrong call in a cold start. |
| Role | Forced by the probe: hidden tools produce no error, so only this text can explain the silence. |
| DO NOT | Four failure modes the corpus shows repeatedly, each phrased as a rule with a route. |

## How it gets tested

Doc 04's acceptance check #1: a cold-start session, no skill loaded, one sentence of intent, driving
a seeded project to a closed task. **Every wrong call is a defect against this text**, and is fixed
here rather than papered over in a tool description. The line count is re-measured each time it
changes; if it needs to exceed 40, something in it is doing a tool description's job.
