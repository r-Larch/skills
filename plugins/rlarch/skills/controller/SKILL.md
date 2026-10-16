---
name: controller
description: Drive a QUEUE of large, mostly independent undertakings to completion — a backlog of bugs, issues, TODOs, migrations or features — by sequencing them, briefing a nested Claude for each, steering it, independently verifying what it produced, landing it, and recording the decisions in the WorkState store (the `work_*` MCP tools), which this skill requires. One level above /rlarch:orchestrate: orchestrate executes ONE plan inside a session, a controller runs many plans across sessions and owns the integration, the verification and the record. USE FOR - a backlog too large for one session; several medium-to-large units that are unrelated to each other; work where each unit needs its own full context window; anything where you must be able to say afterwards what was done, what was deferred and why. DO NOT USE FOR - a single plan (use /rlarch:orchestrate), a task that fits one subagent (use the Agent tool), or a queue you intend to do yourself.
---

# Controller

You are about to run a **queue**, not a task. Units arrive as a list — bugs, issues, TODOs, a
migration split across services — and each one is large enough to deserve its own context window and
its own verification. Your job is not to write the code. It is to decide the order, brief each unit,
check what comes back, land it, and leave a record that outlives the session.

**The three tiers, so you pick the right one:**

| | Scope | Runs in | You own |
|---|---|---|---|
| `Agent` tool | one task | your session | the result |
| `/rlarch:orchestrate` | **one plan**, too large for one window | your session, delegating to subagents | the plan and its verification |
| **controller** (this) | **many plans**, mostly independent | separate nested `claude` sessions, each free to orchestrate | sequencing, briefing, verification, integration, the record |

`/rlarch:nested-claude` is the **mechanics** of running those sessions — launching, addressing, the
READY/BLOCKED/DONE protocol, permissions, worktrees, watchdogs, recovery. Read it before your first
launch. This skill is the **discipline** of running the queue on top of it, and does not repeat it.

> Every rule below was paid for. Most of them cost a run to learn.

---

## 0. Preflight — before anything else

**Call `work_brief`.** The queue, its order, the evidence and the record live in WorkState; this skill
has no other place to keep them.

- **No `work_*` tools in your list** → tell the human in one line: *"controller needs the WorkState
  MCP server and this session cannot see it — run `/rlarch:workstate-setup`."* Then stop. There is no
  markdown fallback.
- **`work_brief` exists but `work_constrain` does not** → the server is older than this skill. Say so
  in one line, point at `/rlarch:workstate-setup`, and stop.
- **It answers** → read it. If a queue is already in flight, its NEXT line is where you resume — with
  `/rlarch:nested-claude` §8 for the sessions that were running when yours stopped.

**Refs come from the server** — `work_brief`, `work_next`, `work_tree`, `work_search`, or the result of
the `work_create` that made them. Never invent one, in a call or in a brief.

---

## 1. The queue is the unit of work

**The queue is a run; each unit is an item under it.** Create them top-down, one `work_create` per
level, reading back the refs each call prints: the project if none exists, the queue as a `run`
(`parent:` the project ref), then each unit as a `task` (`parent:` the queue ref) — its outcome as `criteria`, the gate you will run yourself as `verify_cmd`, and
where it is specified in `intent`. A unit's nested session may run `/rlarch:orchestrate` and keep its
own run for the inside of the unit; the unit item is yours, and only you close it.

The queue's standing rules — what no unit may touch, the verification commands, the stack facts —
are `work_constrain(scope: "<queue ref>", …)`, written once.

Before any agent starts, do three things with the whole list.

**Classify each unit as eligible or escalating.** Eligible means: the outcome is decided, it is a
change you can verify, and it is confined to what you control. Escalating means it needs a decision
that is not yours — a product call, a schema or data change, a dependency, or a unit whose root cause
is still open. **An agent handed an undecided question will decide it**, quietly and probably wrong.
An escalating unit is `work_status(ref, "blocked", reason: "<the decision it needs, and whose>")`
until it is answered.

**Sequence by what unblocks what, not by severity.** Severity orders your *reporting*; dependency
orders your *work*. The highest-value ordering decision is usually:

> **Fix the diagnostic before the diagnosis.**

If one unit is "failures report their cause uselessly" and another is "something fails and we do not
know why", the first is not lower priority than the second — it is a *prerequisite*. Doing it first
turns the second from guesswork into reading. Look for that shape every time; it is common and easy
to miss because the diagnostic fix looks cosmetic.

Write the order into the store, not into a list: `work_link(from: "<prerequisite>", to: "<unit>",
type: "blocks")` for every real dependency, and `priority` for the rest. `work_next` then hands you
the next unit in that order.

**Get one approval for the queue, then run it.** Present `work_tree(scope: "<queue ref>")` — the
eligible units, the blocked ones with what each needs, and your proposed order. Then run to completion without asking
again unless an escalation trigger fires (§8). Asking per unit burns the human's attention on
decisions they already delegated.

### Splitting a unit

A unit is not all-or-nothing. When it mixes a contained fix with a design change — "handle the error"
plus "restructure how errors propagate" — **split it**: brief the part that stops the bleeding, and
name the deferred part in the brief as **your decision, with the reason**. An agent given two of four
items and no explanation will either do all four or stop to ask.

Record the split where the unit is described, not only in your own notes: the unit's `intent` says
what was deferred and why, and the deferred half becomes its own item — `work_create` a `followup`
under the queue, or `work_status(ref, "deferred", target: …)` if it already exists — so it cannot
quietly disappear. The next person to read it must find out *why* half of it is undone without having
to reconstruct your reasoning.

---

## 2. One at a time

Run **one agent at a time** unless the units are genuinely independent all the way down. Shared state —
a database, a branch, a build output directory, a lock file — turns two agents into failures that
belong to neither, and chasing those costs more than the parallelism saves.

Each launch starts the same way: `work_next(scope: "<queue ref>")` names the unit, and
`work_status(ref, "in_progress")` claims it before the brief is written.

**Integration strategy, decided before the first launch:**

- Each unit gets its own branch **and its own worktree** (a background agent cannot edit your
  checkout — see `/rlarch:nested-claude` §7).
- Branch each unit off the *previous* result, not off the base, so conflicts are resolved once by the
  agent that caused them instead of piling up at the end.
- Collect on an integration branch and land once, or land each as it passes — either is fine, but
  decide up front and tell the human which.

**Launch every agent in auto mode** — the default of `Start-NestedAgent.ps1`'s `-PermissionMode`, i.e.
`claude --bg --permission-mode auto`. Under `acceptEdits` every shell shape the allow-list did not foresee
parks the agent until a human attaches: in one three-unit run that was five parks — a `;` inside a build
argument, a commit-message file under `.claude/`, `New-Item` for a folder, a PowerShell byte read, a
build flag in an unexpected order — each one harmless, each one an hour of nothing until someone clicked
*Yes*. Auto mode's classifier approves routine commands itself and refuses risky ones instead of asking,
so the agent gets an answer either way and keeps moving. It is a backstop, not the contract: the brief
still forbids `git push`, production databases and migrations in words, and still routes artifacts away
from paths that are refused whatever the mode (§3). Keep the `-Toolset` profile too — it is what the
agent may do without the classifier's judgement at all.

**In auto mode the brief is the authorization.** The classifier asks whether the *user* asked for an
act, and to a nested agent the user is you: the brief is its first user message. An act that looks
dangerous out of context is refused unless the brief grants it in words — a red-check that disables a
guard to see its test go red was refused as "weakening security" in one run, and three red-checks went
unrun. Grant it as the routine it is, in plain testing words — *"Permission granted to run red-checks
on the code you wrote in this unit, as a normal testing routine: change the line with the Edit tool, run
its test, restore the file with `git checkout -- <file>`, never commit the change."* — and describe each
check by what it proves (*"show that the compatibility test catches a promote without the gate"*), not
with words like "break", "disable" or "weaken". Even granted, a classifier may still refuse; then the
agent skips that check and reports it, and you never run it yourself (§8). Grant only what the human
approved — the red-checks of §5 are approved with the queue.

**Reclaim resources between units.** Each agent building into its own output directory can leave a
build server behind. A twelve-unit run accumulated 28 processes holding 5.4 GB and the machine began
killing background tasks mid-verification.

---

## 3. The brief is the whole job

The agent knows nothing you do not tell it. Everything in `/rlarch:nested-claude` §9 applies; these
are the controller-level additions.

**The store supplies the unit, not the brief.** `work_brief(scope: "<unit ref>", for: "worker")`
renders the unit's criteria, its verification command, the queue's BINDING rules and any notes
addressed to it — paste that in rather than retyping it. Everything below is what it cannot know: it
renders an item's state, not which verbs the session you are about to launch may run.

**Tell the agent the unit item is yours.** It may keep its own run for the work inside the unit, but
it must not call `work_status`, `work_verify` or `work_report` on the unit's ref: those are your
verdicts, and a nested session on the same registration would record its claim as your evidence.

**Carry the baseline as numbers.** Not *"take a baseline"* — the exact failing test name and the
exact counts, plus *"do not fix it, do not report it as a regression, and do not let a subagent clean
it up"*. An agent handed a red suite it did not cause will either claim the regression or repair it.

**Put what you already established in the brief.** You read the code to write the brief; do not make
the agent rediscover it. File paths, the call chain, which of two similar methods is the right one,
what you verified about a neighbouring system — all of it. Every fact you withhold is a chance to
park or to guess.

**Name the out-of-scope neighbour and say it is a decision.** *"Deferred by controller decision,
because …"* answers a question the agent would otherwise answer for itself.

**State what must NOT change**, in words. Implication is not instruction. *"Do not create a worktree"*,
*"do not switch branches"*, *"these five existing tests must pass unmodified"* — and for that last one,
add: **if you cannot make them pass without editing them, STOP and report that**. Editing a test so a
change fits is how a fix becomes a regression with a green tick.

### Every prohibition needs a route

This is the single most expensive lesson available here. **A brief rule that demands a capability the
agent does not have is a trap you set yourself.** In one run, eleven launches were lost to rules added
to prevent real problems:

| Prohibition that parked an agent | The rule that works |
|---|---|
| "no pipes" | "no pipes — narrow with the tool's own `--filter`" |
| "commit with `-F <file>`" | "…to a path **inside your worktree**" |
| "delete the scratch file afterwards" | "leave it; the controller removes it" — deletion was never granted |
| "do not change that warning count" | "the controller checks the count" — it had no way to read one |
| "no `git -C`" | "use `Glob`/`Grep`/`Read` to find files" — nothing had named the alternative |

Sometimes the better move is not to grant the route but to **remove the need for it**: an agent handed
the three file paths it would have gone looking for cannot park looking for them.

**Name the environment's benign noise too.** An agent parks wherever the environment says something it
cannot interpret and the brief is silent.

---

## 4. Steering

Steer when the agent drifts scope, invents a cause the brief did not claim, edits a test instead of
the code, or violates a convention you stated. Anchor the steer to the source — *"re-read the section
on X"* beats a paragraph of new instruction, which competes with the brief rather than correcting it.

**Escalate instead of steering** when the answer is not yours to give.

**Read the comments the agent writes as claims to check.** One agent documented a new flag as *"set by
the kiosk pick, the menu node, and the tool"* — and the tool never set it. The comment was the only
thing that revealed the gap; the code, the tests and the report were all internally consistent without
it. An agent describing what it built is telling you what it *believes* it built.

**Your steer can be wrong, and a wrong steer gets baked into a commit message.** Verify a claim before
you send it. If you find out afterwards, send the correction *before* it commits, and say plainly which
part was wrong. The agent will take your word over the code — which is exactly why your word has to be
checked. Twice in one run a steer dismissed something the agent was right to investigate.

**A parked agent cannot be steered.** Your message queues behind the prompt it is waiting on. It also
cannot tidy up after itself — so a red-check mutation it injected is still sitting in its working tree.
Check the tree before you act on a stopped agent.

---

## 5. Verification is the controller's job

**Never take the agent's word for green.** Not because agents lie — the reports are usually accurate —
but because the things that matter most are the things no report mentions.

Run the build and the suite yourself, then read the diff against what the unit asked for. Record
every run you make — red ones too — with `work_verify(ref: "<unit ref>", result_line: "<the line it
printed>", passed: …, red_check: …)`; only your runs satisfy the unit's close. What you find in the
diff is a finding: `work_finding(findings: [{target: "<unit ref>", title, severity, source:
"review"}])`, and a blocker holds the unit open until it is dealt with. Look for:

- **A test that was edited rather than added.** Updating an assertion because a type legitimately
  changed is fine; weakening one is not. The difference is visible and worth the minute.
- **A red-check mutation left behind.** An agent that parked mid-red-check has the *broken*
  implementation in its tree. Merging that ships a no-op that passes review by commit message.
- **Raw control bytes, encoding damage, whitespace-only churn.** One fix was behaviourally correct
  while writing literal NULs into source files; `file` called them `data` and nothing in the
  transcript looked wrong. Check a huge diff with `--ignore-all-space` before believing it.
- **A number that does not match.** Warning counts, test totals, file counts. Chase the difference to
  its cause rather than accepting it; twice this proved a false alarm and once it was real.

**Mind how YOU edit the tree.** Finishing a parked agent's work means editing files yourself, and a
read-then-write script silently rewrites line endings. Harmless in markdown; **not** harmless in any
language where a raw or verbatim string literal carries its file's endings into runtime output. That
changed the text of a prompt sent to a model, invisibly, and was caught only by a snapshot test whose
expected value was transcribed literally rather than captured.

### When the agent cannot run the gate

Sometimes the check that matters is one the agent has no way to execute — a typecheck with no installed
dependencies, a suite needing a service, a build only you can do. Do not skip it and do not accept an
unverified leg.

**Make it predict the gate's output, precisely enough to be wrong.** Then run the gate and compare. A
match is evidence; a mismatch is a finding. Either way it had to reason about the whole surface rather
than the line it changed.

**Where no harness exists and building one is scope creep, demand an enumeration.** For a five-line
handler, "write a test" means building a rig around something that has none. What you want is: *what
does this produce for every shape the input can take?* A table of cases checked against the diff
catches more than a happy-path test, and twice it surfaced edges the author had not stated.

**Where nothing can catch a wrong guess, redundancy is the gate — and the agent should say so.** One
agent could not confirm an API's field names by reading, and noted that a typecheck could not catch a
wrong name either because the objects were untyped. So it recorded the value it needed from two
independent points, each guarded, so either alone sufficed and a wrong name degraded to the old
behaviour rather than throwing. That is the right shape for an unverifiable assumption.

### The tests that matter most pass in both directions

A test that goes red when the fix is removed proves the fix does something. A test that **passes
before and after** is the one that proves the fix did not do too much. Demand both, and read the
red-check results for the ones that *did not* move — those are the over-correction guards, and a fix
that degrades everything to a safe default will pass every other test in the file.

Ask for red-check variants that are *plausible wrong implementations*, not strawmen: the shape someone
would actually write. The most valuable one is usually the variant that **makes the symptom disappear
while causing different damage** — it is the one that would have shipped.

A red-check that removes an authorization check, a validation or a refusal reads like sabotage to an auto
mode classifier. Grant red-checks in the brief in routine testing words (§2), or they are refused and the
guard goes unproven.

---

## 6. Landing

Verify, merge, remove the worktree, delete the branch — in that order; a worktree pins its branch.
**Stop the agent before removing its worktree**, or the removal fails halfway and leaves the tree on
disk and out of the registry.

A landed unit closes with the commit that landed it: tick its criteria with `work_update(ref,
criteria: [{id, checked: true, evidence}])`, then `work_status(ref, "done", commit: "<sha>")`. The
close is refused until every criterion is checked, one of *your* verifications exists and no blocker
finding targets it; the refusal names the call for each gap.

Green units land. Red or blocked units **do not** — leave the branch in place, record why with
`work_status(ref, "blocked", reason: …)`, and move on. A half-fix on the integration branch costs more
than an open unit.

At the end, run the full gate on the integrated result, not only per unit, and record it with
`work_verify` against the queue. Two changes that each passed alone can still fail together.

---

## 7. The record is a deliverable

The queue's durable output is not the commits — it is the account of what happened. Write it into the
store per unit, **as you go**, not at the end:

| What | Where |
|---|---|
| what the agent says it did — its `DONE` report | `work_report(ref, status, files, verify, red_check, deviations, assumptions, risks, not_done)`; each assumption becomes an open one |
| the verification numbers you measured yourself | `work_verify` — one per run, red included |
| the red-check: each variant and what it defeated | `red_check:` on the `work_verify` that ran it |
| the **reasoning** behind any judgement call | `work_note(kind: "decision", from: "<unit ref>", body: …)` |
| what was deliberately **not** done, and why | a deferred item — `work_status(ref, "deferred", target: …, reason: …)` or a `followup` |
| **your own mistakes** | `work_note(kind: "correction", from: "<unit ref>", body: …)` |
| the agent (name, id, branch, outcome) | `work_note(kind: "environment", from: "<unit ref>", body: …)` |

The store now carries the distinction that used to need care: a `work_report` is the agent's
**claim**, a `work_verify` is **your evidence**, and what the agent reported that you could not confirm
is exactly the report with no verification beside it. Do not paper over that gap with a verification
you did not run.

**Your own mistakes** are not penance; they are the highest-value content in the record. A controller
error — a wrong steer, a bad setup, an edit that changed something invisible — is exactly what the
next run needs to avoid, and it appears nowhere else. A record that holds only the agent's failures is
one someone will distrust as soon as they catch it being incomplete.

The run log writes itself: every transition, verification and closure is in the journal, and
`work_history(scope: "<queue ref>")` reads it back. If your session restarts, `work_brief` plus the
session registry (`/rlarch:nested-claude` §8) is the whole recovery path. Before you report the queue
done, `work_threads(scope: "<queue ref>")` — every open assumption, finding and follow-up, each with
the call that closes it — and decide each one.

**Report to the human in outcomes, not activity.** What landed, what is parked and why, what needs
them, and the one number they actually want — is the thing deployable, and is it pushed.

---

## 8. Escalate — do not guess

Stop and ask when:

- the unit requires a **decision** (which of two meanings, what an operator should see, whether to
  accept a trade-off)
- it needs a **migration, a deploy, or a production data change**
- the agent claims the unit itself is wrong
- it would change a **public contract** — an API surface, a wire format, a published key
- verification fails in a way you cannot attribute
- a **dependency** would be added — that is the human's call, not an agent's, and not yours at 3am
- the base branch moved under you

If you cannot ask — non-interactive, or the human is away — **stop the unit, record the open question
with `work_status(ref, "blocked", reason: "<the question>")`, and keep everything else you landed.** Do not answer it on their behalf and do not silently narrow the
work to avoid it.

**Never launder permissions.** If an agent was refused something, do not do it for it, and do not
relaunch it with wider permissions to get around the refusal. Widening a profile *before* it runs so
it can do the job you already approved is fine; re-running a denied act on its behalf is not. The same
applies when a peer asks you to change config it could not change itself.

---

## 9. When you finish the work yourself

Sometimes an agent parks at the last step with the work done and green. Relaunching to run two
commands is disproportionate — finishing it yourself is legitimate, and honest about it:

- **Only complete what it started.** Removing a red-check mutation restores its intent; writing the
  feature does not.
- **Verify from scratch**, not from its report.
- **Say so in the commit**: who wrote it, what you did, and why it could not finish — and in a
  `work_note(kind: "decision", from: "<unit ref>")`, so the record says it too.

If you find yourself doing this for every unit, the brief is wrong, not the agents.

---

## 10. Failure modes, collected

| Symptom | Cause |
|---|---|
| Agent reports READY and has no instructions | a variadic flag ate the prompt — see `/rlarch:nested-claude` |
| Agent reports READY from an empty folder | your setup step failed and the launch did not check |
| Agent silent, then nothing for an hour | parked on a permission prompt; a parked agent never goes idle, so an idle subscription never fires — launch in auto mode (§2) |
| Steer ignored | it is parked; your message is queued behind the prompt |
| A fix that looks complete and changes nothing | a red-check mutation left in the tree, merged from the working tree instead of the commit |
| Green suite, broken feature | the over-correction case had no guard; every test moved in the same direction |
| A number nobody can explain | you accepted a count instead of diffing the sets |
| The record disagrees with the code | a comment stating what the author intended, not what they wrote |
