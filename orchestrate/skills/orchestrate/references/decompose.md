# Decomposition — phases, tasks, and the run artifacts

Sizing is the highest-leverage decision the orchestrator makes. Tasks that are too big blow the worker's context and come back half-done; tasks that are too small burn agents on bookkeeping and fragment the design across contexts that can't see each other.

## The right-sized task

A task is right-sized when **all** of these hold:

- One agent can carry it **end-to-end** in a single context: understand → change → verify.
- It leaves the repo in a **coherent state** — builds, and doesn't break existing tests.
- It has a **single verification command** whose green/red actually means something for this task.
- Its intent fits in **two sentences**, and its done-criteria in **≤6 checkboxes**.
- It touches roughly **≤10 files**, or more only when the change is genuinely mechanical and repetitive.

### Split when
- The task crosses a contract that doesn't exist yet (define the contract in task N, consume it in N+1).
- Two halves have genuinely different verification (backend logic vs. UI wiring).
- One half is high-risk/high-judgment and the other is mechanical — don't make one agent context-switch between modes.
- The file count is large *and* the changes are non-uniform.

### Do NOT split when
- The split produces an intermediate state that can't compile or can't be verified. A broken intermediate means the next worker starts by debugging its predecessor — the most expensive failure mode in this whole system.
- The two parts must be designed together to be coherent (e.g. an interface and its only implementation, where the shape is still being discovered).
- The split exists only to make tasks look tidy in the plan.

### Watch for the mis-sized task
The characteristic failure is sizing a **contract question** as mechanical work: "apply the code we already wrote to the second consumer" sounds like a copy, but if the two consumers disagree about what the contract *means*, it's three attempts and two reviews. If a task's real content is "decide what the rule is", size it as judgment work and expect a review.

## Phases

A phase is a group of tasks that together deliver an **observable slice** of the goal — something you could demo, or at minimum describe as "X now works". Phases exist so the gate has something meaningful to review; a phase whose completion you can't describe in outcome terms is really just a bag of tasks.

Order phases so that:
1. Contracts, schema, and shared types come before their consumers.
2. Risky/uncertain work comes early — discover a wrong plan in phase 1, not phase 5.
3. Each phase leaves the repo shippable-ish (green build, green tests).

Typical shape: 2–6 phases, 2–6 tasks each. If you're producing 15 phases, you're planning too fine; if 1 phase with 20 tasks, you're not planning at all.

When you group tasks into a phase, note **where they touch each other** — the shared state, the shared key, the ordering dependency. That list is what the phase gate reviews. A phase whose tasks touch nowhere doesn't need a gate.

## PLAN.md

**When a detailed spec already exists, `PLAN.md` is a thin task list that points at it.** Do not restate the spec. Maintaining two descriptions of the same work means you update one, trust the other, and eventually find they disagree.

```markdown
# <Undertaking name>

## Goal
<One paragraph, outcome language: what is true when this is done, and how a
human would confirm it. This is the yardstick for every review.>

## Source of truth
<Path to the spec, if one exists. Everything below is a task list over it,
not a second copy of it.>

## Non-goals
<Explicitly out of scope. Prevents worker and reviewer scope creep.>

## Open assumptions
<Anything decided without conclusive evidence, with the reversal cost.>

## Phases

### Phase 1 — <observable slice>
Gate: <what must be true for this phase to be accepted>
Seams: <where these tasks touch — shared state, keys, ordering. "none" is a
        valid answer and means the gate can be skipped.>
Verify: <command>

- [ ] **1.1 <task name>** — <one-sentence intent>
      Files: <known/likely> · Done: <the criteria> · Verify: <command>
- [ ] **1.2 …**

### Phase 2 — …
```

Repo conventions, stack facts, and safety rules do **not** go here — they go in `CONVENTIONS.md`, once, and every brief points at that file.

Update `PLAN.md` in place when re-planning; the ledger records *that* it changed and why.

## LEDGER.md

The handoff artifact: assume the next reader is a fresh orchestrator with zero context after a compaction. See `assets/LEDGER.template.md`.

**Log by exception.** The recovery payload is the header block — status, open assumptions, deferred follow-ups. That is what a fresh orchestrator actually needs. The task log is a table:

```markdown
| Task | Verdict | Commit | Verify | Note |
|---|---|---|---|---|
| 1.1 schema | ACCEPTED | a1b2c3d | `dotnet test --filter Schema` 12/12 | — |
| 1.2 resolver | ACCEPTED | e4f5a6b | `pnpm ts` ✅ | used existing `X` helper |
```

Write a prose entry **only** when there is something a table row can't carry:
- a deviation that changed the plan;
- an assumption with a non-trivial reversal cost;
- a re-plan;
- a failure, and what the next attempt changed.

**If the commit message already says it, don't repeat it here.** Ledger prose that reads well and decides nothing is the largest avoidable cost in a run — it is insurance against a compaction that usually doesn't happen, and eight lines buy the same recovery as thirty.

Maintain at the top:
- **Status** — current phase/task, and what the next action is.
- **Open assumptions** — the live ones, so they can be revisited when evidence arrives.
- **Deferred follow-ups** — the running list, so nothing quietly disappears.
- **Orchestrator edits** — the zero-behaviour changes you made yourself.

## Re-planning

When a re-plan trigger fires:
1. Write what reality turned out to be — one paragraph in the ledger.
2. Edit `PLAN.md` — remaining phases/tasks only; never rewrite history of completed work.
3. **If a committed spec was proven wrong, add a task to fix the spec.** Fixing only the code leaves the team artifact lying to the next reader.
4. Tell the user what changed and why, briefly. Don't ask permission unless the scope or cost moved materially.
5. Resume the loop.
