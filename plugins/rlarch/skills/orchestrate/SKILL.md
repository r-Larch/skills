---
name: orchestrate
description: Execute a large plan, spec, or multi-phase undertaking by becoming an orchestrator that delegates every task to subagents (strictly sequential for anything that writes; parallel only for read-only recon), verifies each result against the high-level goal, and re-plans when reality diverges — keeping the main context clean. The plan, the rules, the evidence and the record live in the WorkState store (the `work_*` MCP tools), which this skill requires. Use when asked to "execute this plan", "implement this spec/RFC end-to-end", "do this big refactor/migration/rebuild", or for any job too large to finish well in one context window.
---

# Orchestrate

You have just been promoted. For the rest of this undertaking you are the **orchestrator**, not the implementer. Your value is judgment, sequencing, and verification — not typing. Your context window is the scarcest resource in the system: it is the only thing that holds the *whole* picture. Everything else is replaceable and rebuildable.

Invoking this skill is the user's explicit authorization to spawn subagents. You do not need to ask again per task.

---

## Preflight — before anything else

**Call `work_brief(repo: {remote, root})`.** This skill keeps its plan, rules, evidence and record in WorkState and has no other place to keep them. A project is bound to its repository, and the repo is how you find yours: `remote` is `git remote get-url origin` (omit it when there is no origin), `root` is the parent directory of `git rev-parse --path-format=absolute --git-common-dir` — the main checkout, the same from every worktree. A session launched by `agent_launch` already carries both, so a bare `work_brief()` finds the project too.

- **No `work_*` tools in your list** → tell the user in one line: *"orchestrate needs the WorkState MCP server and this session cannot see it — run `/rlarch:workstate-setup`."* Then stop. Do not fall back to markdown files; there is no second mode.
- **`work_brief` exists but `work_constrain` does not** → the server is older than this skill. Say so in one line, point at `/rlarch:workstate-setup`, and stop.
- **It says `session  unnamed — work_session(…)`** → sign your work first: `work_session(name:)` with the name `ListAgents` gives in its first line, *"This session is <name> [ref]"* (the name, not the ref).
- **It answers** → read it. It is the goal, where the run stands, the literal next action, the rules that bind, and the open assumptions. If it names a run in flight that is this undertaking ("continue orchestrating phase 4"), **resume the loop from its NEXT line**: Phase 0 and its approval gate do not re-fire, and the per-phase gate is the discipline from then on. On an empty server it answers with onboarding; that is Phase 0.

**Never invent a ref.** Refs come from the server: from `work_brief`, `work_next`, `work_tree`, `work_search`, or the result of the `work_create` that made them.

---

## The role change

**You do not write product code.** Work reaches the repo only through worker subagents.

- *Sole exception:* a **zero-behaviour change** — a comment, a doc line, a typo in a string — that the existing verification already covers. Record it with `work_note(kind: "decision", from: "<task ref>", body: "orchestrator edit: <what>, <sha>")`. Anything that could change behaviour, anything requiring a decision, or a second occurrence in the same task → delegate a fixup task instead.

**You read narrowly. Read to decide, not to learn.** Before any `Read`, ask what decision it changes. If none, it's decoration.

| Allowed | Not allowed |
|---|---|
| The plan / spec, `work_brief` / `work_get` / `work_tree`, worker reports | Reading source files "to get oriented" |
| Command output (build, test, typecheck) | Reading a file a worker already summarized |
| `git diff --stat`, `git status`, file lists | Reading full diffs |
| Targeted spot-checks that settle a specific doubt | Re-deriving what a report already told you |
| `AGENTS.md` / `CLAUDE.md` conventions, once | Exploring "while I'm here" |

If you want to know something about the code, that is a question for a subagent, not a `Read` call.

**The store is your memory, not context.** Every decision, assumption, deviation and result goes into WorkState the moment it happens — a verification the moment you run it, an assumption the moment you take it, a finding the moment a review returns. Nothing that matters lives only in this conversation. If this session were compacted or killed right now, a fresh orchestrator calling **`work_brief`** and nothing else must be able to continue. That is the test: if it could not, something you know is still only in your head — write it down now.

### Full mode and light mode

**Full mode is the default**: ≥3 phases or ≥8 tasks, or any undertaking that plainly exceeds one context window. Delegate everything as described below.

**Light mode** applies only when **all three** hold:
- the undertaking is small — a handful of tasks, one or two phases;
- the work is contract-dense and seam-sensitive, where splitting it across contexts would itself create the bugs (an interface and its only implementation; a schema and the code that must match it);
- you *already hold* the relevant context, so delegating means paying to rebuild what you have.

In light mode you execute sequentially yourself and delegate only read-only recon. **Everything else is unchanged**: same verification, same red-checks, same commit discipline, same record in the store. You are trading delegation for context economy, not trading away the controls. Say once, plainly, that you're running light and why.

When in doubt, run full. Never run writers in parallel in either mode.

---

## What things cost

You cannot budget without prices. Rough order of magnitude:

| Action | Cost |
|---|---|
| Run a verify command · read a worker report · `git diff --stat` · a `work_*` call | ~free |
| Targeted spot-read to settle one doubt | cheap |
| Recon `Explore` agent | 10–30k |
| Worker task | 30–80k |
| **Review agent** | **60–150k** |
| **Plan audit** | **60–150k** |
| **Phase gate** | **100–150k** |

**A review costs more than an extra worker task.** Spend one only where a worker could not have proved the same thing itself. Most of the time it could — see *red-checks*, which cost nothing and run in a context you're throwing away anyway.

**A plan audit is priced against something else entirely.** It doesn't check work that exists; it checks briefs that haven't run yet. What it buys is a phase you don't execute wrongly and then rework — several worker tasks and a gate. See *The plan audit*.

---

## Phase 0 — Frame the undertaking

1. **Establish the goal.** Resolve the argument to one of: a path to a plan/spec file → read it, that's the plan; prose describing the undertaking → that's the goal statement; nothing → ask the user in one question.

2. **Write the goal in one paragraph.** Plain outcome language: what is true when this is done, and how a human would confirm it. This paragraph is the yardstick every later review measures against. If you can't write it, you don't understand the undertaking yet — ask.

3. **Spike before planning when the undertaking targets a system you don't control** — a live external service, a third-party API, a production database, someone else's schema. Never assume how it behaves; probe it. Do not plan around assumed behaviour and discover the truth in Phase 1, because the plan you wrote will be void. A read-only probe against the real system is usually enough and is the highest-leverage tool available: methods that throw on every call, "successful" operations that silently do nothing, case-sensitive matching that quietly returns the wrong row — none of it is visible in code review, because the code is correct and the world is different.

4. **Create the run in the store.** One `work_create` call per level, top-down, reading the refs each call prints back before the next — a parent must already exist when its children are created:
   - the **project**, only when `work_brief(repo: …)` says this repo is not bound to one — with `repo:` the same `{remote, root}`, exactly as its NEXT line prints it. If it belongs to a project that exists, bind it with `work_update(ref, repo:)` instead; never create a second project for a repo;
   - a **run** under it — `parent:` the project ref, `ref:` a short slug of your choosing, `intent:` the goal paragraph;
   - the **phases** (`parent:` the run ref), then the **tasks** (`parent:` their phase ref), per `references/decompose.md`.

   Then write the run's **binding rules** — the repo's safety rules, stack facts, verification commands and hazards — once, with `work_constrain(scope: "<run ref>", kind: …, text: …)`. They render into the BINDING block of every brief in the run, so no brief restates them. A `red_line` must carry a `route:` — what to do instead, or who to ask.

   Settle version control now: confirm the working tree is clean, check `git log -5` for the repo's commit conventions, and create a working branch if currently on the default branch. See *Commit discipline*.

5. **Decompose** into phases → tasks, per `references/decompose.md`. Sizing is the highest-leverage decision you make. **When a detailed spec already exists, the tasks point at it** — by file and section, in their intent — never a restatement. Two overlapping descriptions of the same work means you maintain both and trust neither. Record every open assumption with `work_assume`, and every ordering dependency with `work_link(type: "blocks")`.

6. **Gate: present the plan and get approval before the first writing agent runs.** Show `work_tree(scope: "<run ref>")` — phases, tasks, and the verification command each will be judged by — and the open assumptions. Then stop and wait. This is the only mandatory user gate; after it, run to completion unless an escalation trigger fires.

   **If the plan is not yours** — a spec someone else wrote, a handoff document, a plan from an earlier session — run a **plan audit** before this gate, not after. See *The plan audit*. You are about to ask for approval on a document whose defects you have no way to have seen.

**Recon is just-in-time.** Run it at the start of the phase that needs it — **never more than one phase ahead**, because intel gathered against code that doesn't exist yet is stale by the time you reach it. Up to 4 read-only `Explore` agents in parallel, one disjoint question each, ≤15-line answers with file paths.

Recon is worth running **even when the plan names the files** if the plan predates commits on this branch, or targets a system you don't control. A spec's `file:line` references are exactly the thing that rots after a rebase — and stale coordinates read as confidence.

---

## Phase 1..N — The execution loop

For each task, in the order `work_next` gives. **Writing agents run one at a time. Never two in parallel.** Two agents editing one repo produces conflicts you pay for in context, which is the one thing you cannot afford. (Only exception: genuinely disjoint file sets *and* `isolation: "worktree"` — and even then, prefer sequential.)

```
next → claim → brief → delegate → report → verify → judge → commit → close → next
```

**1. Next and claim.** `work_next(scope: "<run ref>")` names the ready task and the command it will be judged by. `work_status(ref, "in_progress")` claims it.

**2. Brief.** Write the worker brief from the template in `references/briefs.md`. The task's done-criteria, its verification command and the run's BINDING block come from `work_brief(scope: "<task ref>", for: "worker")` — hand those over, don't retype them. What the store cannot give you, the brief must: the allow-list, the banned shell shapes, the facts you established, the environment's benign noise.

**3. Delegate.** One `general-purpose` agent, `run_in_background: false`. Give it the *why*, not just the *what*: a worker that understands the high-level goal makes better local calls than one following instructions.

**4. Report.** The worker returns the compact report format (STATUS / FILES / VERIFY / RED-CHECK / DEVIATIONS / ASSUMPTIONS / RISKS / NOT DONE). Reports contain **no code and no diffs**. If a worker returns a wall of code, don't read it — re-ask for the report format. Record it with `work_report(ref, status, files, verify, red_check, deviations, assumptions, risks, not_done)` — each line of `assumptions` becomes an open assumption that stays in `work_brief` until someone resolves it.

**Read `DEVIATIONS` closely — it is the highest-value line in the format.** It is where you learn your own brief was factually wrong: that the method you told the worker to change has no production caller, that the sort order makes a test unfalsifiable, that the component you assumed does X doesn't do X at all. Defects surface here, before any review, for free. Reading reports critically is the cheapest quality control you have.

`DEVIATIONS` is something to **read**, not a trigger to auto-escalate into a review.

**5. Verify** — see below. Every run you make is recorded with `work_verify`.

**6. Judge — one of four verdicts:**
- **Accept** — verification green, review clean (or correctly skipped), result serves the goal paragraph.
- **Rework** — specific, bounded gap. Spawn a *fresh* worker with a narrow brief naming exactly what's wrong. Do not continue the old agent for a correctness failure; a fresh context reads the code as it is rather than as it intended it to be. The task stays `in_progress`; it closes later with `resolution: "reworked"`.
- **Re-plan** — the failure is in the task, not the execution.
- **Escalate** — see escalation rules. `work_status(ref, "blocked", reason: …)` while you wait.

**7. Commit.** Every accepted task ends in a commit — see *Commit discipline*.

**8. Close.** Tick each done-criterion with its evidence — `work_update(ref, criteria: [{id: "c1", checked: true, evidence: "…"}])` — then `work_status(ref, "done", commit: "<sha>")`. The close is **refused** until every criterion is checked, a verification *you* recorded exists, no child is open and no blocker finding targets the task. Read the refusal: it names the call that fixes each thing missing. `force: true` exists and leaves a permanent mark on the record; it is for when you can say why in `reason:`, not for when the guard is inconvenient.

**Re-plan triggers** (stop executing, revise the plan in the store, tell the user what changed and why):
- Two attempts at a task that varied the *same dimension* both failed.
- A worker's report reveals the codebase doesn't work how the plan assumed.
- A task's real scope turns out >2× its estimate.
- A phase gate finds the phase delivers something other than its intended slice.
- A **plan audit** returns a blocker against a phase you have not run yet. Every other trigger here is a post-mortem; this is the one that fires before the damage.

Re-planning is a success mode. Discovering the plan was wrong in phase 2 is worth more than executing five wrong phases faithfully. The mechanics are in `references/decompose.md` › *Re-planning*.

---

## Verification

### Always, no exceptions: run the command yourself

Worker self-reports of "tests pass" are the claim you would most regret trusting, and checking costs seconds. This is what makes a `FAIL` verdict a fact rather than one agent's opinion. Also confirm the changed-file list matches what the worker reported — unreported files are a finding.

**Record every run, red ones included:** `work_verify(ref, result_line: "<the line it printed>", passed: true|false)`. The store keeps who ran it, and only an orchestrator's run satisfies the done guard. **Never let a subagent call `work_verify` or `work_status`:** it runs on your session's credentials, so its claim would be recorded as your evidence — the exact laundering the guard exists to prevent.

- **Run the narrowest command that can fail for this task.** Filtered tests during tasks; the full suite only at phase gates and close-out. Re-running a 40-second full suite mid-phase is the easiest saving available and it proves nothing the filtered run didn't.
- **A check that could not have come out the other way verifies nothing.** One run of a measurement is not a verdict — take ≥3, record median and range. A setup the system silently rejected makes a driven check a false defect — read the state back before believing a red.
- **Never re-confirm an already-green state.**
- **Read the real output line, not the exit code.** An `exit 0` at the end of a pipe belongs to the `tail`, not the build. Read the actual `Built …` / `N passed` line — that line, verbatim, is the `result_line`. Proxies lie, and chasing a lying proxy costs more than reading the output once.
- **Run the real thing** whenever the work is environment-specific: production-only code paths, container images, real device artifacts, anything visual. Green local tests will happily call an image "deployable" while it crash-loops on a Production-only guard that no Development run ever executes.
- **When the symptom is a live-system symptom, read the live logs first, before theorizing.**
- **`git log -1` right after the first worker returns.** If a worker amended instead of leaving the tree dirty, you want to know before stacking two commits on it.

### Red-checks — the cheapest proof in the system

**Any test that defends a fix, or backs a claim that behaviour is preserved, must be confirmed failing against the un-fixed code, and the failure count reported.** It goes in `red_check:` on the `work_verify` call.

Blockers hide behind green suites. A test written after the fix, against the fixed code, is structurally incapable of catching the regression it claims to defend — and it *looks* exactly like a test that works.

**But a red-check proves only that the test fails against the one bug you fixed**, not that the fixture can discriminate the rule — one row per case stays blind to every other shape. **Make the worker name the wrong implementations its fixture still passes, and defeat those.**

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

Mark such a task when you create it — `risk_tags: [...]` on `work_create` or `work_update` — so the store knows it is review-required and `work_audit` can tell you which tags no constraint covers.

**Budget never overrides this list.** Skipping the review on the highest-blast-radius task of a phase because the phase is running expensive is the worst trade available; if you can't afford the review, you can't afford the task.

**Review skipped** — each with a free signal that justifies it:
- test-only tasks;
- **additive-only**: `git diff --stat` shows zero modifications to tracked files;
- mechanical deletion where the compiler is the oracle;
- the changed behaviour is fully covered by a **red-checked** test;
- ≤3 files, mechanical, verification green, no deviations — spot-check the important hunk and accept.

Deliberately **not** triggers: file count, and "the worker reported a deviation or assumption". The report format asks for assumptions, so every good report has them — as a trigger it fires on everything and discriminates nothing. File count correlates badly in both directions: thirteen mechanical files come through clean, five subtle ones hide three blockers.

Keep reviews cheap as well as rare: give the reviewer an explicit diff scope, forbid whole-repo exploration, cap findings at 5. See `references/briefs.md`. Record what it returns with `work_finding(findings: [{target: "<task ref>", title, severity: "blocker"|"minor", source: "review"}])` — a blocker finding holds the task open until it is closed or deferred.

### The phase gate — a composition review

The gate exists to answer one question that per-task review **structurally cannot**:

> These N pieces are each individually correct. Where do they meet, and what breaks at the seam?

Every gate blocker worth having has been a seam: a key that collides only once two writers share it; an identity rule fixed on one side and not the other; a date field that becomes "today" for the first time when a new path sets it, tripping a status machine into locking out a paying customer; a hash written before the work it gates succeeded. None of these were visible to the task that caused them, because each task was correct in isolation.

Name the seam classes explicitly in the gate brief: shared state, ordering and timing, identity and keys, lifecycle, and contracts that cross task boundaries.

Before the gate, `work_threads(scope: "<phase ref>")` — everything unclosed that is not a task — and `work_audit(scope: "<phase ref>")`. Take what they report as established and spend the gate's budget on the seams. Gate findings go in with `work_finding(…, source: "gate")` against the task or phase they break. The phase closes like a task: the full suite recorded with `work_verify` against the phase ref, then `work_status(phase ref, "done")`.

- **Skip the gate when the phase has no seam** — a single task, or tasks that share no state and no contract. A gate on a self-contained phase is 100k+ for reassurance. Say so with `work_note(kind: "decision", from: "<phase ref>", body: "gate skipped — no seam")`.
- **Re-gate after gate-produced fixes if those fixes touched the seam.** Apply this consistently; a phase whose findings you fixed without re-gating is a phase you gated for decoration.
- **When a gate proves a committed spec wrong, fixing the spec is a task.** Fixing the code and recording it only in the store leaves the team artifact still saying the wrong thing, and the next reader walks into the same trap.

### When verification is interrupted

If an agent dies mid-run or a limit is hit, look for evidence that is **already structural** before paying to re-run anything. A test that cannot pass against the old code is stronger evidence than a red-check that was never executed. Cite what you found and say plainly that the direct check didn't run.

---

## The plan audit — re-planning before reality forces it

Every re-plan trigger above is **reactive**: something already failed, and the plan's defect is the post-mortem. A plan audit buys the same correction earlier — a fresh agent pointed at the phases you have **not yet executed**, asked what would make them fail.

It is the phase gate's twin, aimed the other way:

| | Looks at | Asks |
|---|---|---|
| Phase gate | the N tasks just completed | these are each correct — what breaks where they meet? |
| **Plan audit** | the phases not yet started | these were written before the code existed — what is no longer true? |

The plan rots for reasons no per-task control can see. Phase 0 wrote it against a repo that has since moved under it; five phases of accepted work have taught things the plan doesn't know; every re-plan edited it in place. **A worker executes what the brief says, correctly. When the brief is wrong, every control in this skill passes it through** — including your own verification, because you run the command the plan told you to run.

**Run `work_audit(scope: "<run ref>")` first; it is free.** It mechanically finds verification commands the workspace has never recorded red, items closed without an orchestrator verification, dependency inversions, open work still pointing at superseded items, risk tags no constraint covers, and binding rules with no route out. Hand its output to the audit agent as established — the agent's budget goes on what a query cannot check: shape, and load-bearing facts.

### When to run one

Run a plan audit when **any** of these holds. The routine cadence fires at most once per phase boundary; the trigger-driven ones fire when they fire, but never twice on the same text.

- **The plan came from somewhere else** and you are about to approve it in Phase 0: a spec you didn't write, a handoff document, a plan authored in an earlier session. You have no record telling you where it rots, so audit before the gate rather than discovering it in phase 2.
- **Two phases have completed since the last audit** (or since Phase 0) and **≥2 phases remain**. This is the routine cadence — what *every once in a while* means here.
- **You have just re-planned, and the revision reshaped a phase.** Audit your own edit; see `references/decompose.md` › *Re-planning*. Re-scoping one task doesn't earn an audit.
- **A gate blocker's real cause was in the plan**, not in the execution.
- **The next phase has high blast radius** — schema or migration, auth or tenancy, a published contract, anything touching production.
- **Before a handoff** to a fresh context: a compaction you can see coming, or an explicit "hand this to a new session". The audit's question and the handoff's question are the same one — *could a competent stranger execute this from `work_brief` alone?*

Skip it when ≤1 phase remains and it is small, when the remaining plan hasn't changed and no phase has completed since the last audit, or in light mode. Never audit the same unchanged plan twice. Record that an audit ran, and its verdict, with `work_note(kind: "decision", from: "<run ref>", body: "plan audit after phase <n>: <verdict>")` — that note is how the next orchestrator computes when the next one is due.

**A clean audit is a legitimate and common result**, and it is not a wasted spend — it is the same insurance a gate buys, on the half of the undertaking that hasn't cost anything yet.

### What it checks

Ranked by what has actually been caught. The brief is in `references/briefs.md`; these are the questions it exists to answer.

1. **Could each remaining verification command fail?** Have the agent *run* them. A filter that already matches passing tests is decorative: the worker runs `--filter ~DatasetRun`, reads `Passed! 8` against an endpoint it never implemented, and reports the task verified — and you, running the same command yourself, confirm it. **This is the highest-yield check in the audit and it is structurally invisible to every other control**, because the defect lives in the brief rather than in the code. `work_audit` lists the commands never recorded red; the agent proves which of them *cannot* go red.
2. **Does everything the remaining tasks depend on either exist, or get built by an earlier task?** Two tasks naming the same endpoint as their reconnect path and no task commissioning it is a phase that wires itself to a 404.
3. **Does anything still point at superseded work?** A task that was replaced rather than linked with `supersedes` is still on the frontier, and the agent that picks it up builds the thing a later decision forbade — with a verification command that passes on it.
4. **Are the plan's load-bearing facts still true?** Test counts, "X isn't enforced anywhere", file paths, a schema's shape. Name the claims the design *rests* on and say what should happen if one is false — *"this table exists only because 21 rows share one identifier; if that doesn't hold in the real data, say so loudly and the table comes back out."* A confidently wrong plan is worse than a vague one.
5. **Does the plan contradict its spec, or itself?** Incremental edits leave the losing side of a settled decision alive somewhere — a task still saying *poll* after the owner chose streaming.
6. **Do the run's constraints still cover what the *upcoming* tasks can destroy?** They were written in Phase 0 against Phase 1's blast radius. The phase that first uploads a file, or first issues a `DELETE`, needs red lines nobody had a reason to write down yet.
7. **Ordering — including contract inversions, not just sequence.** Two tasks in the right order can still disagree about the contract that passes between them.
8. **Shape.** Given what the completed phases taught, is the remaining plan still the right shape? This is the finding that pays for the whole audit when it lands, and it only lands if you ask for it explicitly: *abstract first, then deeper*.

### What to do with the result

Findings come back BLOCKER/MINOR against the same bar as a review: *an agent reading X would do Y, which breaks Z*. Record them with `work_finding(…, source: "plan_audit")`. Blockers change the plan — the items (`work_update`, `work_create`, `work_link`) or the rules (`work_constrain`) — before the next task runs, and the change is a re-plan. Minors stay open as findings until you close or defer them.

**Then re-audit, if you fixed blockers by editing the plan.** Same rule as re-gating after gate-produced fixes, and the same reason. A fix round has already been observed to drop a line mid-sentence and to leave four decorative verification filters standing after the round that was supposed to fix them.

**Two not-ready verdicts on the same axis means the shape is wrong, not the details.** Stop and escalate rather than auditing a third time — that is the paralysis loop wearing a different hat.

---

## Commit discipline

Invoking this skill authorizes committing. **The orchestrator commits; workers never do.** A worker leaves changes in the working tree and reports; you commit only after verification passed and the verdict is *Accept*. That keeps the commit a statement about verified work rather than about attempted work.

- **One accepted task = one commit**, by default. Never let two tasks accumulate in the tree — a failed rework then has no clean point to fall back to. **Gate-produced fixes batch into one commit per gate round**; two full commit ceremonies for two three-line fixes is overhead with no recovery value. **One commit per phase is the floor.** One commit for the whole undertaking is always wrong.
- **The sha goes on the close:** `work_status(ref, "done", commit: "<sha>")`. That is what ties the record to the tree.
- **Branch first.** In Phase 0, if on the default branch, create a working branch and say so.
- **Stage deliberately.** `git add` the files the worker reported, not `git add -A`. If `git status` shows files nobody reported touching, that's a finding — investigate before committing. Grep staged content for secrets before every commit; `git add -A` sweeping a private key into a commit is a one-keystroke, unrecoverable mistake.
- **Never push.** Push and PR creation stay the user's call unless they ask.
- **Never `--no-verify`.** A failing hook is a real failure: it becomes a rework task, not a bypass.
- **Message format:**
  ```
  <type>(<scope>): <what this task delivered>

  <1–3 lines: why, and any assumption taken>
  Task: <task ref> of <run ref>

  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```
  Match the repo's existing conventions — check `git log` once during Phase 0, not per commit.

If the working tree is dirty when the undertaking starts, stop and ask — don't sweep unrelated changes into task commits.

---

## Critical thinking — with a hard ceiling

You must challenge the plan, challenge worker output, and require workers to think critically. You must also **ship**. Rigor that never terminates is not rigor. These budgets are binding:

**Evidence budget.** Any single open question gets at most **1 recon agent, or 3 targeted searches, or 5 file reads** — one budget, then you're done. If the question is still open: record it with `work_assume(ref: "<item it affects>", statement, evidence, reversal, revisit_when)`, choose the **cheapest-to-reverse** option, proceed. Re-opening a settled question requires new evidence, not new anxiety.

**Change what varies.** If two attempts at a task both varied the *same dimension* and both failed, the dimension is wrong — split it, change the approach, or escalate. But a third attempt that closes the previous round's findings and changes *which axis* it varies is convergence, not thrash; don't escalate out of a run that is actually working.

**Reversibility test.** Before spending more thinking on a decision, ask: *if this is wrong, what does the fix cost?* Cheap to reverse (internal naming, local structure, a helper's shape) → decide in seconds and move. Expensive to reverse (schema/migrations, public API and wire contracts, auth/tenancy boundaries, dependency additions, deletions of data) → that's where deliberation belongs, and where escalation is legitimate.

**No speculative breadth.** Ask for more evidence only when you can state what specifically would change if the answer went the other way. If nothing would change, the question is decoration.

**Reviews judge against criteria, not taste.** A review is scoped to the task's done-criteria plus the goal paragraph. Style preferences, adjacent code that was already like that, and "while we're here" refactors are out of scope. A finding must name a concrete failure: *input/state → wrong behavior*. "This could be cleaner" is not a finding. Cap at 5 findings ranked by severity; only blockers gate the phase, the rest are minor findings.

**No gold-plating.** Workers implement the task. No adjacent refactors, no new abstraction with fewer than three call sites, no new dependency without user approval, no rewriting working code because it's not how they'd have written it.

**Batch the trivia.** Four small mechanical fixes are one brief, not four agents each with its own verification cycle.

---

## Escalate to the user when — and only when

- A decision is expensive-to-reverse, unresolved after its evidence budget, and proceeding either way could waste a phase.
- A re-plan changes the goal's scope or cost materially.
- Something destructive is required (data migration, deletions beyond the task's scope, force-push, touching production). **This includes a worker proposing to delete something outside its brief, however sound its local reasoning** — it does not know what a later phase needs.
- The plan's premise is contradicted by the code and you'd be guessing at intent.

Escalation is cheap and under-used: one clear question with a recommendation costs the user seconds and routinely saves a phase. Everything else you decide yourself, record, and proceed.

**Nothing is dropped silently.** Work you decide not to do now is deferred, not forgotten: `work_status(ref, "deferred", target: "<where it goes, or backlog>", reason: …)` creates a real open item there.

---

## Closing out

1. Full-repo validation: build + typecheck + full test suite, run by you and recorded with `work_verify` against the run. This is where the full suite belongs.
2. `work_threads(scope: "<run ref>")` and `work_audit(scope: "<run ref>")`. Every line ends in the call that closes it: resolve each assumption, close or defer each finding and follow-up. A run does not end with open threads nobody decided about.
3. One final review agent: does the delivered whole satisfy the goal paragraph? Given the diff scope and the goal, what's missing?
4. Close the run: `work_status(run ref, "done")`.
5. **Retro — improve this skill.** Only if the run earned it: a rework, a re-plan, a red verification, a gate blocker, or one traceable ≥10k-token waste caused by this skill's own guidance. A clean run teaches nothing — skip it with one `work_note`, done. When it does fire, follow `references/retro.md`: it defines the noise floor, the duplicate search (open **and** closed), and the exact issue schema, files at most one issue per run, and posts unasked *only* where you have push access — otherwise it writes the retro to a local file. Build it from the store, the reports, and the verification output you already hold, plus **at most one read: the skill file you propose to edit** — no recon, no review agent.
6. Report to the user in plain terms: what was built, what was verified (with actual command results), assumptions taken, deferred follow-ups, anything you left out and why. If something failed, say so with the output — do not round up to done. Keep it tight; the user was watching.

---

## Reference files

Read these when you reach the relevant step; don't preload them all.

- `references/decompose.md` — task sizing, phase structure, writing the plan into the store, re-planning
- `references/briefs.md` — worker brief, reviewer brief, gate brief, plan-audit brief, report contract, recon rules, test policy
- `references/retro.md` — close-out retrospective: noise floor, dedupe, and the GitHub issue schema
