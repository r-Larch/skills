# 04 — Self-explaining to a coding agent

> **The goal**, stated by the owner: *a working MCP that is self-explaining in how to use it to a
> coding agent.* This is the acceptance bar, not a polish item. A tool surface an agent has to be
> taught by a skill file has failed it.

The test: **a fresh Claude Code session, with no skill loaded and no instructions from the user
beyond "use workstate to run this work", drives a full decompose → execute → verify → close cycle
without a wrong call and without asking how.**

**Four** channels carry the explanation — a fifth, MCP prompts, was measured out of existence (§5).
They are ordered by how reliably a model reads them.

---

## 1. `ServerInstructions` — the one that lands in the system prompt

**Measured end-to-end, 2026-09-18.** `McpServerOptions.ServerInstructions` reaches the model **in
full**: under a top-level `# MCP Server Instructions` heading with a `## <server-name>` sub-heading,
all 15 lines of the probe block intact, **not truncated**, and repeated **once per registered
server**.

That last detail sets the budget: the ≤40-line cap is **per registration**, and a session with the
server registered twice pays twice. Keep it tight anyway — it is in context for every turn.

It carries only what cannot be discovered from a tool description:

1. **The loop**, in one line: `work_brief` → `work_next` → claim → work → `work_verify` →
   `work_status done`. Everything else is a detour off that spine.
2. **Start every session with `work_brief`.** It is the recovery payload; nothing else needs reading.
3. **The closure contract**, stated plainly: *done is refused without checked criteria, an
   orchestrator verification, and no open children or blocker findings. Deferring creates a real item
   — nothing can be dropped silently.*
4. **Refs are short and scoped** to your run; a qualified `run/ref` works from anywhere.
5. **Read the `attention` block.** It is the only thing in a result that is about work you are not
   currently doing.
6. **What not to do**: do not re-read a document you already have; do not create a task without a
   verification command that can fail; do not write a status you did not verify.

It must **not** restate the tool list, the argument schemas, or anything `tools/list` already says.
Duplicating the schema is how these instructions rot.

---

## 2. Tool descriptions that teach the sequence

Each description answers three questions in ~3 lines: *what it does*, *when to reach for it*, and
**what to call next**. The third is what turns 20 independent tools into a workflow.

```
work_next — The ready frontier: items whose blockers are all done, in order.
Call after work_brief, or after closing an item. Returns each item's verification
command and risk flags, so you can size it before claiming.
Next: work_get(ref) for the full brief, then work_status(ref, "in_progress").
```

Annotations are part of the explanation, not metadata: `ReadOnly = true` tells an agent it can call
`work_tree` freely; `Destructive = true` on `cancel` tells it not to.

### The `Next:` line of every tool — this is the workflow graph

The set has to **chain**. A tool whose `Next:` leads nowhere is a dead end an agent falls out of, and
the only way to see that is to write them all down together.

| Tool | `Next:` |
|---|---|
| `work_brief` | whatever its own `NEXT` line says — usually `work_next`, or `work_status(in_progress)` on a run already under way |
| `work_next` | `work_get(ref)` for the detail, then `work_status(ref, "in_progress")` |
| `work_get` | `work_status(in_progress)` to start it · `work_verify` if it is already built · `work_brief(ref, for:"worker")` to hand it to someone |
| `work_tree` | `work_next` to act, or `work_threads` for what is unclosed |
| `work_search` | `work_get(ref)` on a hit |
| `work_threads` | the call named on each line — `work_assume`, `work_status`, `work_note` |
| `work_audit` | `work_update` / `work_link` to fix what it found; `work_note` to record what you are not fixing |
| `work_history` | `work_get(ref)` on anything surprising |
| `work_create` | `work_link` if it depends on something · `work_next` to start |
| `work_update` | `work_status` when the criteria are met |
| `work_move` | `work_tree` to confirm the shape |
| `work_link` | `work_next` — the frontier has just changed |
| `work_status(in_progress)` | do the work, then `work_verify` |
| `work_status(done)` | `work_next` |
| `work_status(deferred)` | `work_next` — and note the new item it created |
| `work_patch` | `work_get(ref, include:"body")` to confirm |
| `work_report` | `work_verify` — a report is a claim, not evidence |
| `work_verify` | `work_status(ref, "done")` |
| `work_note` | nothing; it is a side-effect, and saying so stops an agent hunting for a follow-up |
| `work_assume` | nothing, unless resolving it unblocks an item |
| `work_finding` | `work_status(ref, "in_progress")` on the target, or `work_status(finding, "deferred", target:…)` |

**Three properties to check whenever a tool is added or changed**, because they are what make the
graph a workflow rather than a menu:

1. **Every tool's `Next:` names a tool that exists.** Trivially testable; assert it.
2. **Every path leads back to `work_next` or to `work_brief` within two hops.** Those are the two
   orientation points; a tool more than two hops from either is somewhere an agent gets lost.
3. **`work_report → work_verify` is stated in those words.** *"A report is a claim, not evidence"* is
   the single most load-bearing sentence in the whole set — it is the skills' *"worker self-reports
   of 'tests pass' are the claim you would most regret trusting"*, delivered at the exact moment an
   agent is about to trust one.

---

## 3. Errors that are instructions

Every refusal names the remedy as a **callable line**, because a tool execution error is the one
message a model is guaranteed to read and act on. Doc 01 §4 shows the shape:

```
REFUSED: 2.4 → done. 2 conditions unmet.
  · criterion c2 unchecked
      → work_update(ref:"2.4", criteria:[{id:"c2", checked:true, evidence:"…"}])
  · no orchestrator verification (worker-reported only)
      → work_verify(ref:"2.4", command:"…", result_line:"…", run_by:"orchestrator")
```

An agent that hits this once has learned the closure rule permanently, at the cost of one round trip
— which is a better teacher than any paragraph, because it arrives exactly when it is relevant.

**Design rule:** no error may say only what is wrong. Every one names the call that fixes it. This is
`nested-claude`'s hardest-won lesson — *"a rule that demands a capability the agent does not have is a
trap you set yourself"*; every prohibition ships with its route.

---

## 4. `work_brief` as the tutorial

The first call of every session returns, besides the state, a **`next action`** line naming a
concrete tool call. An agent that only ever does what `work_brief` tells it is running the system
correctly. On an **empty** server it returns an onboarding brief instead — how to create a project,
what a good `verify_cmd` looks like, and the one call to make next — so the cold-start path is
explained by the same mechanism as every other path.

---

## 5. ~~Prompts as the long form~~ → `work_brief(for: …)` as the long form

**Settled by measurement, 2026-09-18. This channel does not exist.** MCP prompts are invisible in
Claude Code — absent from the tool list, absent from the skills listing, not invokable. The
prediction above ("the channel with the least certainty") was correct, and the fallback named there
is now the design: **the detailed doctrine is a `for:` variant of `work_brief`** (doc 01 §9).

So the count is **four channels, not five**, and every one of them is measured to work:
`ServerInstructions` · tool descriptions · errors-as-instructions · `work_brief` output.

Losing the fifth made the design better, not worse. A prompt is something a *human* picks from a
menu; a brief is something an *agent* needs mid-task. As a tool call it sits where the work is,
composes with `work_next`, and is one fewer primitive to explain in a budget measured in lines.

**One thing the probe added to this document's job.** Because `[Authorize]` *hides* a tool rather
than refusing it (doc 01 §6), a worker never receives an error explaining what it may not do — the
tool simply is not there. So §3's "errors that are instructions" cannot cover role boundaries, and
`ServerInstructions` **must** state the role: *"you are a worker; closing a task is the
orchestrator's call."* Without that line, the agent hunts for a tool that does not exist from where
it stands, and nothing tells it why.

---

## Acceptance — how we prove it

Not by assertion. Three checks, each of which can fail.

### The contamination trap — read this before running check 1

The cold-start test is the goal's only real measurement, and it is **trivially easy to run in a way
that proves nothing.** Every agent in this project so far has been driven by a brief, and a brief is
instruction. If the test prompt explains the loop, names a tool, or hints at an order, the agent
succeeds using *the prompt* and the result says nothing about the server.

That is the skills' own rule — *"a check that could not have come out the other way verifies
nothing"* — pointed at ourselves.

**The protocol:**

1. A **fresh** session. The server registered, a seeded project present, **no skill loaded**, no
   `CLAUDE.md` describing WorkState, nothing in the repo that documents the tools.
2. **The entire prompt is one sentence of intent**, and it must be *outcome* language a user would
   actually type. Permitted:
   > *"Use workstate to pick up the next task on the gaia entity-store run and take it to done."*

   **Forbidden in the prompt:** any tool name, the word `work_brief`, the order of operations, the
   closure conditions, the existence of an attention block, the word "ref". If the prompt would
   still teach a server-less agent what to do, it is contaminated.
3. **No steering.** The controller does not answer, correct, or hint. If the agent parks or gives
   up, that is the result, and the result is a failure of a channel.
4. **Record every call in order**, and classify each: *correct* · *wrong but self-corrected from a
   refusal* · *wrong and not corrected*. A wrong call the server's own refusal fixed is a **pass for
   the error channel** — that is §3 working exactly as designed, and it is the cheapest teacher in
   the system.
5. **Bar: zero calls in the third class.** Each one is a defect against the specific channel that
   should have prevented it, and it is fixed **there** — in `ServerInstructions`, in a tool
   description, or in a refusal — never by adding an explanation to the prompt.

Run it **at least twice, with different seeded states** (one mid-run, one empty server). A single
transcript is an anecdote; the second run is what catches a channel that only worked by luck.

### The three checks

1. **Cold-start transcript**, per the protocol above. **Zero uncorrected wrong calls.**
2. **Closure cannot be faked.** An attempt to close an unverified task must be refused, and the
   refusal must be **sufficient on its own** for the agent to recover on the next call — no second
   hint from the controller. Verify the agent does not instead reach for `force: true`; if it does,
   the refusal is explaining the escape hatch better than the remedy, and it is the refusal that is
   wrong.
3. **Token budget**, all three as numbers, recorded and re-measured on change:
   `ServerInstructions` ≤40 lines · the full `tools/list` payload · and `work_brief` on the imported
   `entity-store` run against the **6,011 lines** of `LEDGER.md` it replaces. That last ratio is the
   project's headline claim, so it is a build-failing test, not a sentence in a README.
