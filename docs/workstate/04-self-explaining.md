# 04 — Self-explaining to a coding agent

> **The goal**, stated by the owner: *a working MCP that is self-explaining in how to use it to a
> coding agent.* This is the acceptance bar, not a polish item. A tool surface an agent has to be
> taught by a skill file has failed it.

The test: **a fresh Claude Code session, with no skill loaded and no instructions from the user
beyond "use workstate to run this work", drives a full decompose → execute → verify → close cycle
without a wrong call and without asking how.**

Five channels carry the explanation. They are ordered by how reliably a model reads them.

---

## 1. `ServerInstructions` — the one that lands in the system prompt

**Verified:** `McpServerOptions.ServerInstructions` (string) flows into `DiscoverResult.Instructions`
/ `InitializeResult.Instructions`. Claude Code renders these into the session under a
**"MCP Server Instructions"** heading — this document's author can see exactly that happening for
three other servers in the current session, so it is observed behaviour, not a hope.

This is the highest-value real estate in the whole design and it has a hard budget: it is in context
for **every turn of every session**. Target **≤40 lines**. It carries only what cannot be discovered
from a tool description:

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

## 5. Prompts as the long form

Doc 01 §9's prompts (`worker_brief`, `gate_brief`, `plan_audit_brief`, `decompose`) are where the
detailed doctrine lives — rendered on demand, with live state injected, rather than sitting in
context. This is the channel with the **least certainty**: whether Claude Code surfaces server
prompts usefully is exactly what task 0.2 settles. If it does not, their content moves into
`work_brief`'s output and into tool descriptions, and nothing is lost but elegance.

---

## Acceptance — how we prove it

Not by assertion. Three checks, each of which can fail:

1. **Cold-start transcript.** A fresh session, no skill, one sentence of user intent, driving a
   seeded project to a closed task. Record every wrong call. **Zero wrong calls is the bar**; each
   one is a defect against the channel that should have prevented it, and is fixed there.
2. **Closure cannot be faked.** A scripted attempt to close an unverified task must be refused, and
   the refusal must be sufficient for the agent to recover unaided on the next call.
3. **Token budget.** `ServerInstructions` ≤40 lines; the full `tools/list` payload measured and
   recorded; `work_brief` on the imported `entity-store` run measured against the **6,011 lines** it
   replaces. That ratio is the project's headline claim and it gets a number, not an adjective.
