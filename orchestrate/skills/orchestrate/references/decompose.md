# Decomposition — phases, tasks, and the plan artifact

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

## Phases

A phase is a group of tasks that together deliver an **observable slice** of the goal — something you could demo, or at minimum describe as "X now works". Phases exist so the phase gate has something meaningful to review; a phase whose completion you can't describe in outcome terms is really just a bag of tasks.

Order phases so that:
1. Contracts, schema, and shared types come before their consumers.
2. Risky/uncertain work comes early — you want to discover a wrong plan in phase 1, not phase 5.
3. Each phase leaves the repo shippable-ish (green build, green tests).

Typical shape: 2–6 phases, 2–6 tasks each. If you're producing 15 phases, you're planning too fine; if 1 phase with 20 tasks, you're not planning at all.

## PLAN.md

```markdown
# <Undertaking name>

## Goal
<One paragraph, outcome language: what is true when this is done, and how a
human would confirm it. This is the yardstick for every review.>

## Non-goals
<Explicitly out of scope. Prevents worker and reviewer scope creep.>

## Constraints & conventions
<Pointers to CLAUDE.md / AGENTS.md / instruction files. Stack facts workers
need but shouldn't have to rediscover. Verification commands for this repo.>

## Open assumptions
<Anything decided without conclusive evidence, with the reversal cost.>

## Phases

### Phase 1 — <observable slice>
Gate: <what must be true for this phase to be accepted>
Verify: <command>

- [ ] **1.1 <task name>** — <one-sentence intent>
      Files: <known/likely> · Done: <the criteria> · Verify: <command>
- [ ] **1.2 …**

### Phase 2 — …
```

Update `PLAN.md` in place when re-planning; the ledger records *that* it changed and why.

## LEDGER.md

Append-only. This is the handoff artifact — assume the next reader is a fresh
orchestrator with zero context after a compaction. See `assets/LEDGER.template.md`.

Entry per task, kept compact:

```markdown
### 1.2 <task name> — ACCEPTED (commit a1b2c3d)
Worker: general-purpose · Attempt 1
Files: src/foo.ts (new resolver), src/bar.ts (wire-up)
Verify: `pnpm run ts` ✅ · `dotnet test --filter Foo` ✅ 12/12
Deviations: used existing `X` helper instead of new util (worker's call, agreed)
Assumptions: tenant id always present on this path — reversible, local guard added
Follow-ups: no test for the empty-collection branch → deferred to 3.1
```

Also maintain at the top of the ledger:
- **Status** — current phase/task, and what the next action is.
- **Open assumptions** — the live ones, so they can be revisited when evidence arrives.
- **Deferred follow-ups** — the running list, so nothing quietly disappears.
- **Orchestrator edits** — the ≤3-line unblocks you made yourself.

## Re-planning

When a re-plan trigger fires:
1. Write what reality turned out to be (one paragraph in the ledger).
2. Edit `PLAN.md` — remaining phases/tasks only; never rewrite history of completed work.
3. Tell the user what changed and why, briefly. Don't ask permission unless the scope or cost moved materially.
4. Resume the loop.
