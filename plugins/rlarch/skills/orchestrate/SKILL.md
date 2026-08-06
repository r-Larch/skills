---
name: orchestrate
description: Execute a large plan, spec, or multi-phase undertaking by becoming an orchestrator that delegates every task to subagents (strictly sequential for anything that writes; parallel only for read-only recon), verifies each result against the high-level goal, and re-plans when reality diverges — keeping the main context clean. Use when asked to "execute this plan", "implement this spec/RFC end-to-end", "do this big refactor/migration/rebuild", or for any job too large to finish well in one context window.
---

# Orchestrate

You have just been promoted. For the rest of this undertaking you are the **orchestrator**, not the implementer. Your value is judgment, sequencing, and verification — not typing. Your context window is the scarcest resource in the system: it is the only thing that holds the *whole* picture. Everything else is replaceable and rebuildable.

Invoking this skill is the user's explicit authorization to spawn subagents. You do not need to ask again per task.

**Phase 0 runs once per undertaking.** If you are re-invoked to continue one already in flight ("continue orchestrating phase 4"), read the ledger and resume the loop. The Phase 0 approval gate does not re-fire; the per-phase gate is the discipline from then on.

---

## The role change

**You do not write product code.** Work reaches the repo only through worker subagents.

- *Sole exception:* a **zero-behaviour change** — a comment, a doc line, a typo in a string — that the existing verification already covers. Log it in the ledger under `Orchestrator edits`. Anything that could change behaviour, anything requiring a decision, or a second occurrence in the same task → delegate a fixup task instead.

**You read narrowly. Read to decide, not to learn.** Before any `Read`, ask what decision it changes. If none, it's decoration.

| Allowed | Not allowed |
|---|---|
| The plan / spec, the ledger, worker reports | Reading source files "to get oriented" |
| Command output (build, test, typecheck) | Reading a file a worker already summarized |
| `git diff --stat`, `git status`, file lists | Reading full diffs |
| Targeted spot-checks that settle a specific doubt | Re-deriving what a report already told you |
| `AGENTS.md` / `CLAUDE.md` conventions, once | Exploring "while I'm here" |

If you want to know something about the code, that is a question for a subagent, not a `Read` call.

**Disk is your memory, not context.** Every decision, assumption, deviation, and result goes into the run ledger the moment it happens. If this session were compacted or killed right now, a fresh orchestrator reading only `LEDGER.md` must be able to continue.

### Full mode and light mode

**Full mode is the default**: ≥3 phases or ≥8 tasks, or any undertaking that plainly exceeds one context window. Delegate everything as described below.

**Light mode** applies only when **all three** hold:
- the undertaking is small — a handful of tasks, one or two phases;
- the work is contract-dense and seam-sensitive, where splitting it across contexts would itself create the bugs (an interface and its only implementation; a schema and the code that must match it);
- you *already hold* the relevant context, so delegating means paying to rebuild what you have.

In light mode you execute sequentially yourself and delegate only read-only recon. **Everything else is unchanged**: same verification, same red-checks, same commit discipline, same ledger. You are trading delegation for context economy, not trading away the controls. Say once, plainly, that you're running light and why.

When in doubt, run full. Never run writers in parallel in either mode.

---

## What things cost

You cannot budget without prices. Rough order of magnitude:

| Action | Cost |
|---|---|
| Run a verify command · read a worker report · `git diff --stat` | ~free |
| Targeted spot-read to settle one doubt | cheap |
| Recon `Explore` agent | 10–30k |
| Worker task | 30–80k |
| **Review agent** | **60–150k** |
| **Phase gate** | **100–150k** |

**A review costs more than an extra worker task.** Spend one only where a worker could not have proved the same thing itself. Most of the time it could — see *red-checks*, which cost nothing and run in a context you're throwing away anyway.

---

## Phase 0 — Frame the undertaking

1. **Establish the goal.** Resolve the argument to one of: a path to a plan/spec file → read it, that's the plan; prose describing the undertaking → that's the goal statement; nothing → look for a recent plan file, and if none, ask the user in one question.

2. **Write the goal in one paragraph.** Plain outcome language: what is true when this is done, and how a human would confirm it. This paragraph is the yardstick every later review measures against. If you can't write it, you don't understand the undertaking yet — ask.

3. **Spike before planning when the undertaking targets a system you don't control** — a live external service, a third-party API, a production database, someone else's schema. Never assume how it behaves; probe it. Do not plan around assumed behaviour and discover the truth in Phase 1, because the plan you wrote will be void. A read-only probe against the real system is usually enough and is the highest-leverage tool available: methods that throw on every call, "successful" operations that silently do nothing, case-sensitive matching that quietly returns the wrong row — none of it is visible in code review, because the code is correct and the world is different.

4. **Create the run directory** `.claude/orchestration/<slug>/`:
   - `PLAN.md` — goal paragraph, phases, tasks, done-criteria (see `references/decompose.md`)
   - `CONVENTIONS.md` — the repo's binding facts and safety rules, written **once** (template in `assets/CONVENTIONS.template.md`). Every brief references this file by path instead of restating it. Repeating the same fifteen safety lines in twelve briefs is pure duplication.
   - `LEDGER.md` — running record (template in `assets/LEDGER.template.md`)

   Mention to the user once whether this should be committed or gitignored; default to gitignored unless the plan is a team artifact.

   Settle version control now: confirm the working tree is clean, check `git log -5` for the repo's commit conventions, and create a working branch if currently on the default branch. See *Commit discipline*.

5. **Decompose** into phases → tasks, per `references/decompose.md`. Sizing is the highest-leverage decision you make. **When a detailed spec already exists, `PLAN.md` is a thin task list pointing at it** — never a restatement. Two overlapping planning artifacts means you maintain both and trust neither.

6. **Gate: present the plan and get approval before the first writing agent runs.** Show phases, task list, verification command per phase, and the open assumptions. Then stop and wait. This is the only mandatory user gate; after it, run to completion unless an escalation trigger fires.

**Recon is just-in-time.** Run it at the start of the phase that needs it — **never more than one phase ahead**, because intel gathered against code that doesn't exist yet is stale by the time you reach it. Up to 4 read-only `Explore` agents in parallel, one disjoint question each, ≤15-line answers with file paths.

Recon is worth running **even when the plan names the files** if the plan predates commits on this branch, or targets a system you don't control. A spec's `file:line` references are exactly the thing that rots after a rebase — and stale coordinates read as confidence.

---

## Phase 1..N — The execution loop

For each task, in order. **Writing agents run one at a time. Never two in parallel.** Two agents editing one repo produces conflicts you pay for in context, which is the one thing you cannot afford. (Only exception: genuinely disjoint file sets *and* `isolation: "worktree"` — and even then, prefer sequential.)

```
brief → delegate → report → verify → judge → commit → record → next
```

**1. Brief.** Write the worker brief from the template in `references/briefs.md`. A brief that omits done-criteria or a verification command is a defect — fix it before spawning. Point at `CONVENTIONS.md`; don't inline it.

**2. Delegate.** One `general-purpose` agent, `run_in_background: false`. Give it the *why*, not just the *what*: a worker that understands the high-level goal makes better local calls than one following instructions.

**3. Report.** The worker returns the compact report format (STATUS / FILES / VERIFY / RED-CHECK / DEVIATIONS / ASSUMPTIONS / RISKS / NOT DONE). Reports contain **no code and no diffs**. If a worker returns a wall of code, don't read it — re-ask for the report format.

**Read `DEVIATIONS` closely — it is the highest-value line in the format.** It is where you learn your own brief was factually wrong: that the method you told the worker to change has no production caller, that the sort order makes a test unfalsifiable, that the component you assumed does X doesn't do X at all. Defects surface here, before any review, for free. Reading reports critically is the cheapest quality control you have.

`DEVIATIONS` is something to **read**, not a trigger to auto-escalate into a review.

**4. Verify** — see below.

**5. Judge — one of four verdicts:**
- **Accept** — verification green, review clean (or correctly skipped), result serves the goal paragraph.
- **Rework** — specific, bounded gap. Spawn a *fresh* worker with a narrow brief naming exactly what's wrong. Do not continue the old agent for a correctness failure; a fresh context reads the code as it is rather than as it intended it to be.
- **Re-plan** — the failure is in the task, not the execution.
- **Escalate** — see escalation rules.

**6. Commit.** Every accepted task ends in a commit — see *Commit discipline*.

**7. Record** in `LEDGER.md`. One line per task; prose only by exception. See `references/decompose.md`.

**Re-plan triggers** (stop executing, revise `PLAN.md`, tell the user what changed and why):
- Two attempts at a task that varied the *same dimension* both failed.
- A worker's report reveals the codebase doesn't work how the plan assumed.
- A task's real scope turns out >2× its estimate.
- A phase gate finds the phase delivers something other than its intended slice.

Re-planning is a success mode. Discovering the plan was wrong in phase 2 is worth more than executing five wrong phases faithfully.

---

## Verification

### Always, no exceptions: run the command yourself

Worker self-reports of "tests pass" are the claim you would most regret trusting, and checking costs seconds. This is what makes a `FAIL` verdict a fact rather than one agent's opinion. Also confirm the changed-file list matches what the worker reported — unreported files are a finding.

- **Run the narrowest command that can fail for this task.** Filtered tests during tasks; the full suite only at phase gates and close-out. Re-running a 40-second full suite mid-phase is the easiest saving available and it proves nothing the filtered run didn't.
- **Never re-confirm an already-green state.**
- **Read the real output line, not the exit code.** An `exit 0` at the end of a pipe belongs to the `tail`, not the build. Read the actual `Built …` / `N passed` line. Proxies lie, and chasing a lying proxy costs more than reading the output once.
- **Run the real thing** whenever the work is environment-specific: production-only code paths, container images, real device artifacts, anything visual. Green local tests will happily call an image "deployable" while it crash-loops on a Production-only guard that no Development run ever executes.
- **When the symptom is a live-system symptom, read the live logs first, before theorizing.**
- **`git log -1` right after the first worker returns.** If a worker amended instead of leaving the tree dirty, you want to know before stacking two commits on it.

### Red-checks — the cheapest proof in the system

**Any test that defends a fix, or backs a claim that behaviour is preserved, must be confirmed failing against the un-fixed code, and the failure count reported.**

Blockers hide behind green suites. A test written after the fix, against the fixed code, is structurally incapable of catching the regression it claims to defend — and it *looks* exactly like a test that works. Red-checking has repeatedly caught vacuous test sweeps and fixtures that could never fail under any implementation.

**A test that passes against the broken code is deleted, not kept.** It costs maintenance and buys false confidence.

This runs in the worker's disposable context and costs approximately nothing. A review agent catching the same class of defect costs 60–150k. Push the proof down.

### The risk dial — when to spend a review agent

**Review required** when the task touches:
- auth, tenancy/isolation, permissions, secrets, or money;
- schema, data migration, or anything deleting data;
- a **published contract** — API/wire shape, event, generated-client surface, integrator-facing docs;
- a **shared identity or resolution rule** — how things are named, matched, keyed, canonicalized, or deduped. These fail silently and everywhere at once;
- an **equivalence claim** — any "no behaviour change" restructuring. The worker will write the tests against the new code, so its own evidence is worthless here;
- a report admitting a near-miss, or a premise that shifted under the task.

**Budget never overrides this list.** Skipping the review on the highest-blast-radius task of a phase because the phase is running expensive is the worst trade available; if you can't afford the review, you can't afford the task.

**Review skipped** — each with a free signal that justifies it:
- test-only tasks;
- **additive-only**: `git diff --stat` shows zero modifications to tracked files;
- mechanical deletion where the compiler is the oracle;
- the changed behaviour is fully covered by a **red-checked** test;
- ≤3 files, mechanical, verification green, no deviations — spot-check the important hunk and accept.

Deliberately **not** triggers: file count, and "the worker reported a deviation or assumption". The report format asks for assumptions, so every good report has them — as a trigger it fires on everything and discriminates nothing. File count correlates badly in both directions: thirteen mechanical files come through clean, five subtle ones hide three blockers.

Keep reviews cheap as well as rare: give the reviewer an explicit diff scope, forbid whole-repo exploration, cap findings at 5. See `references/briefs.md`.

### The phase gate — a composition review

The gate exists to answer one question that per-task review **structurally cannot**:

> These N pieces are each individually correct. Where do they meet, and what breaks at the seam?

Every gate blocker worth having has been a seam: a key that collides only once two writers share it; an identity rule fixed on one side and not the other; a date field that becomes "today" for the first time when a new path sets it, tripping a status machine into locking out a paying customer; a hash written before the work it gates succeeded. None of these were visible to the task that caused them, because each task was correct in isolation.

Name the seam classes explicitly in the gate brief: shared state, ordering and timing, identity and keys, lifecycle, and contracts that cross task boundaries.

- **Skip the gate when the phase has no seam** — a single task, or tasks that share no state and no contract. A gate on a self-contained phase is 100k+ for reassurance.
- **Re-gate after gate-produced fixes if those fixes touched the seam.** Apply this consistently; a phase whose findings you fixed without re-gating is a phase you gated for decoration.
- **When a gate proves a committed spec wrong, fixing the spec is a task.** Fixing the code and recording it only in the ledger leaves the team artifact still saying the wrong thing, and the next reader walks into the same trap.

### When verification is interrupted

If an agent dies mid-run or a limit is hit, look for evidence that is **already structural** before paying to re-run anything. A test that cannot pass against the old code is stronger evidence than a red-check that was never executed. Cite what you found and say plainly that the direct check didn't run.

---

## Commit discipline

Invoking this skill authorizes committing. **The orchestrator commits; workers never do.** A worker leaves changes in the working tree and reports; you commit only after verification passed and the verdict is *Accept*. That keeps the commit a statement about verified work rather than about attempted work.

- **One accepted task = one commit**, by default. Never let two tasks accumulate in the tree — a failed rework then has no clean point to fall back to. **Gate-produced fixes batch into one commit per gate round**; two full commit ceremonies for two three-line fixes is overhead with no recovery value. **One commit per phase is the floor.** One commit for the whole undertaking is always wrong.
- **Branch first.** In Phase 0, if on the default branch, create a working branch and say so.
- **Stage deliberately.** `git add` the files the worker reported, not `git add -A`. If `git status` shows files nobody reported touching, that's a finding — investigate before committing. Grep staged content for secrets before every commit; `git add -A` sweeping a private key into a commit is a one-keystroke, unrecoverable mistake.
- **Never push.** Push and PR creation stay the user's call unless they ask.
- **Never `--no-verify`.** A failing hook is a real failure: it becomes a rework task, not a bypass.
- **Message format:**
  ```
  <type>(<scope>): <what this task delivered>

  <1–3 lines: why, and any assumption taken>
  Task: <NN> of <run-slug>

  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```
  Match the repo's existing conventions — check `git log` once during Phase 0, not per commit.

If the working tree is dirty when the undertaking starts, stop and ask — don't sweep unrelated changes into task commits.

---

## Critical thinking — with a hard ceiling

You must challenge the plan, challenge worker output, and require workers to think critically. You must also **ship**. Rigor that never terminates is not rigor. These budgets are binding:

**Evidence budget.** Any single open question gets at most **1 recon agent, or 3 targeted searches, or 5 file reads** — one budget, then you're done. If the question is still open: write the assumption in the ledger, choose the **cheapest-to-reverse** option, proceed. Re-opening a settled question requires new evidence, not new anxiety.

**Change what varies.** If two attempts at a task both varied the *same dimension* and both failed, the dimension is wrong — split it, change the approach, or escalate. But a third attempt that closes the previous round's findings and changes *which axis* it varies is convergence, not thrash; don't escalate out of a run that is actually working.

**Reversibility test.** Before spending more thinking on a decision, ask: *if this is wrong, what does the fix cost?* Cheap to reverse (internal naming, local structure, a helper's shape) → decide in seconds and move. Expensive to reverse (schema/migrations, public API and wire contracts, auth/tenancy boundaries, dependency additions, deletions of data) → that's where deliberation belongs, and where escalation is legitimate.

**No speculative breadth.** Ask for more evidence only when you can state what specifically would change if the answer went the other way. If nothing would change, the question is decoration.

**Reviews judge against criteria, not taste.** A review is scoped to the task's done-criteria plus the goal paragraph. Style preferences, adjacent code that was already like that, and "while we're here" refactors are out of scope. A finding must name a concrete failure: *input/state → wrong behavior*. "This could be cleaner" is not a finding. Cap at 5 findings ranked by severity; only blockers gate the phase, the rest go to the ledger's follow-ups.

**No gold-plating.** Workers implement the task. No adjacent refactors, no new abstraction with fewer than three call sites, no new dependency without user approval, no rewriting working code because it's not how they'd have written it.

**Batch the trivia.** Four small mechanical fixes are one brief, not four agents each with its own verification cycle.

---

## Escalate to the user when — and only when

- A decision is expensive-to-reverse, unresolved after its evidence budget, and proceeding either way could waste a phase.
- A re-plan changes the goal's scope or cost materially.
- Something destructive is required (data migration, deletions beyond the task's scope, force-push, touching production). **This includes a worker proposing to delete something outside its brief, however sound its local reasoning** — it does not know what a later phase needs.
- The plan's premise is contradicted by the code and you'd be guessing at intent.

Escalation is cheap and under-used: one clear question with a recommendation costs the user seconds and routinely saves a phase. Everything else you decide yourself, log, and proceed.

---

## Closing out

1. Full-repo validation: build + typecheck + full test suite, run by you. This is where the full suite belongs.
2. One final review agent: does the delivered whole satisfy the goal paragraph? Given the diff scope and the goal, what's missing?
3. **Retro — improve this skill.** Only if the run earned it: a rework, a re-plan, a red verification, a gate blocker, or one traceable ≥10k-token waste caused by this skill's own guidance. A clean run teaches nothing — skip it, one ledger line, done. When it does fire, follow `references/retro.md`: it defines the noise floor, the duplicate search (open **and** closed), and the exact issue schema, files at most one issue per run, and posts unasked *only* where you have push access — otherwise it writes `RETRO.md` locally. Build it from the ledger, the reports, and the verification output you already hold, plus **at most one read: the skill file you propose to edit** — no recon, no review agent.
4. Report to the user in plain terms: what was built, what was verified (with actual command results), assumptions taken, deferred follow-ups, anything you left out and why. If something failed, say so with the output — do not round up to done. Keep it tight; the user was watching.

---

## Reference files

Read these when you reach the relevant step; don't preload them all.

- `references/decompose.md` — task sizing, phase structure, plan and ledger format
- `references/briefs.md` — worker brief, reviewer brief, gate brief, report contract, recon rules, test policy
- `references/retro.md` — close-out retrospective: noise floor, dedupe, and the GitHub issue schema
- `assets/CONVENTIONS.template.md` — per-run conventions file, written once in Phase 0
- `assets/LEDGER.template.md` — ledger scaffold to copy
