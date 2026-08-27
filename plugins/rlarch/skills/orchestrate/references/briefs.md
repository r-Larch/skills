# Briefs — workers, reviewers, gates, recon

## Choosing the agent

| Need | Agent | Parallel? |
|---|---|---|
| Implement a task (writes files) | `general-purpose` | **Never.** One at a time. |
| Answer a scoped question about the codebase | `Explore` | Yes, up to 4, disjoint questions |
| Review finished work | `general-purpose` (review brief) | One at a time |
| Design/architecture options for a hard task | `Plan` | Rarely; usually you decide |
| Audit the phases not yet executed | `Plan` (read-only) | One at a time |

Always `run_in_background: false` for writers and reviewers — you need the result before the next step.

**Fresh worker vs. resumed agent:**
- **Rework after a correctness failure → a fresh worker**, with a narrow brief naming exactly what's wrong. The original's context is anchored on what it *intended* to write; a fresh context reads the code as it actually is. Continue the original only for genuinely additive work ("also handle the null case").
- **Re-verifying a reviewer's own findings → resume that reviewer via `SendMessage`.** It still holds its reproduction harness, so confirming a fix costs a fraction of the original review. Spawning a fresh reviewer to re-check work the first reviewer already understands is paying twice.

---

## The worker brief

Copy this shape. A brief missing done-criteria or a verification command is a defect; fix it before spawning. Point at `CONVENTIONS.md` — never inline the repo's safety rules and stack facts into every brief.

```markdown
## Context
<2–4 sentences. The high-level goal of the undertaking, and where this task sits
in it. Workers who understand the destination make better local calls than
workers following instructions. Include what previous tasks already delivered.>

## Read first — binding
`.claude/orchestration/<slug>/CONVENTIONS.md` — repo conventions, verification
commands, and safety rules for this run. Follow it.

## If the premise is wrong, stop and say so
This brief may be factually wrong about the code. If you find that it is — the
method has no caller, the component doesn't do what I claimed, the approach
can't work — **stop and report that instead of forcing it through.** A correct
"this doesn't work because X" is worth more than a plausible-looking
implementation, and it is the single most useful thing you can return.
Anything marked `unverified` is a claim I did not check — check it before
you build on it.

## Your task
<One or two sentences of intent — the outcome, not a keystroke script.>

## In scope
- <the change>

## Out of scope — do not touch
- <adjacent things that will tempt you>
- No refactors outside the files below. No new dependencies. No new abstractions
  with fewer than three call sites. No "while I'm here" improvements.

## Where to work
Likely files: <paths, if known>
Precedent to follow: <existing file that does the analogous thing>

## Done when
- [ ] <criterion — specific and checkable>
- [ ] <criterion>
- [ ] <tests, if the test policy calls for them — say which behaviors>

## Verify with
`<exact command — the narrowest one that can fail for this change>` — must be
green before you report done. Run it yourself, and read the actual result line,
not an exit code from the end of a pipe.

## Red-check — required for any test defending a fix or a "no behaviour change" claim
Revert your change (stash it, or temporarily restore the old behaviour), run the
test, and confirm it **fails**. Report the failure count. A test that passes
against the broken code cannot catch the regression it claims to defend —
delete it rather than keep it. If your change has several distinct guards or
placements, red-check each one; the placement is often the actual decision.

## How to think
Think critically about the approach, but with a budget: if a design question is
still open after ~3 targeted searches or ~5 file reads, take the
cheapest-to-reverse option, note it under ASSUMPTIONS, and keep moving. Do not
read broadly "to be safe".

## Do not
- Do not commit or push. Leave changes in the working tree; the orchestrator commits.
- Do not delete any file not named in scope above. If deletion looks correct,
  report it and stop — you cannot see what a later phase needs.
- Do not paste code, diffs, or file contents into your report.
- Do not report done if verification is red. Report `blocked` or `partial` with the failure.

## Report back in exactly this format
STATUS: done | partial | blocked
FILES: <path — one line on what changed> (one per line)
VERIFY: <command> → <actual result, e.g. "12 passed" / "2 failed: <names>">
RED-CHECK: <what you reverted> → <n failed> | n/a
DEVIATIONS: <what you did differently from this brief, and why> | none
ASSUMPTIONS: <decisions taken without conclusive evidence> | none
RISKS: <what might bite later> | none
NOT DONE: <anything in scope you did not finish, and why> | nothing

Keep it under 25 lines. Prose beyond this format is noise.
```

**Documentation tasks: enumerate by category, not by filename.** "Update `CLAUDE.md` and `.plans/`" reliably forgets the README that is the repo's front page. Say *"every document a reader could follow to use this"* and let the worker find them.

---

## The review brief

Reviews cost 60–150k. Make each one cheap as well as justified: bound the diff, forbid exploration, cap the output.

```markdown
## What to review
Exactly these changes: <files>, on branch <b>, <SHA range>.
Do not explore the wider repo. Read what you need to judge this diff and stop.

## Judge against exactly this
Goal: <the goal paragraph>
Task intent: <one sentence>
Done-criteria: <the checklist from the worker's brief>

## Out of scope
Style preferences. Code that was already like that before this change.
Improvements unrelated to the criteria above. Anything in the plan's non-goals.

## Bar for a finding
A finding must name a concrete failure: specific input or state → wrong
behavior, crash, security or tenancy leak, data loss, or a criterion not met.
"Could be cleaner", "consider extracting", "might be worth testing" are not
findings. If you cannot describe how it breaks, it is not a finding.

Verify each finding before reporting it — read the actual code path and confirm
the failure is reachable. Report at most 5, ranked, each marked BLOCKER or MINOR.

## Also answer
- Does this actually serve the goal, or does it merely satisfy the letter of the criteria?
- Is anything in the done-criteria unmet or only apparently met?
- Could any test here pass against a deliberately broken implementation?

## Report format
VERDICT: pass | pass-with-minors | fail
FINDINGS: <BLOCKER|MINOR> <file:line> — <failure scenario> (one per line, max 5)
GOAL FIT: <one or two sentences>
No code blocks. Under 20 lines.
```

**For an equivalence claim** ("no behaviour change"), replace the judging section with a field-by-field comparison of old vs. new output for the same input, and say so explicitly. The worker's own tests are worthless as evidence here — they were written against the new code.

---

## The phase gate brief

The gate is a **composition review**. Its job is the question no per-task review can answer.

```markdown
## What this is
Phase <n> is complete: <one line per task, what each delivered>.
Each task was individually verified and accepted. Assume they are correct in isolation.

## The question
These pieces are each correct alone. **Where do they meet, and what breaks at the seam?**
Work through these seam classes concretely, naming the code path for each:
- Shared state — what do two of these tasks both read or write?
- Ordering and timing — does anything now happen in a new order, or for the
  first time, or before something it depends on?
- Identity and keys — do two pieces derive the same key, name, or match
  differently? Can they collide?
- Lifecycle — is anything created, cached, or invalidated by one piece and
  consumed by another?
- Contracts crossing task boundaries — did one side's change reach every consumer?

## Judge against
Goal: <the goal paragraph>
Phase gate criterion: <what must be true for this phase to be accepted>

## Report format
VERDICT: pass | pass-with-minors | fail
FINDINGS: <BLOCKER|MINOR> <file:line> — <failure scenario> (max 5, ranked)
SEAMS CHECKED: <one line each, and what you concluded>
GOAL FIT: <does this phase deliver its slice of the goal?>
Under 25 lines. No code blocks.
```

---

## The plan audit brief

Fires on the cadence in `SKILL.md` › *The plan audit*. Use the `Plan` agent, **read-only**: it audits, you edit `PLAN.md`. Reach for `general-purpose` with write access scoped to the run directory only when the re-shape is large enough that transcribing the answer costs more than making the edit — and then re-audit, because surgery on a long markdown file breaks things.

Three rules separate an audit that pays from one that returns an essay:

- **Hand it the facts, marked as established.** Everything recon and the completed phases already proved goes into the brief as *take these as established, do not re-verify*. An audit that re-derives what you already hold charges full price for information you had.
- **Give it the real controls.** It must be able to run the verification commands and probe the real system. That is how it proves a `Verify:` filter is decorative, and how it checks a claim against the data rather than against the document asserting it.
- **Point it at the ledger and forbid reading it through.** A mature run's ledger is thousands of lines. Say: *search it for specific claims; do not read it cover to cover.*

```markdown
## What this is
<Undertaking, branch, HEAD.> Phases <1..n> are complete, committed and gated; `<verify command>`
is <result>. You are auditing the phases **not yet executed**: <list them>.

**Think abstract first, then go deeper.** A task list that is right in detail and wrong in shape is
worse than useless. If the shape is wrong, say so before enumerating anything.

## Read these — they are the authority
- `.claude/orchestration/<slug>/PLAN.md` — <state explicitly which section supersedes which>
- `.claude/orchestration/<slug>/CONVENTIONS.md` — the binding safety rules
- `<the spec, if one exists>`
- `.claude/orchestration/<slug>/LEDGER.md` — **do not read cover to cover.** Search it for
  specific claims only.

## Established — take as fact, do not re-verify
<What recon and the completed phases already proved: what exists, what doesn't, the precedent to
copy, which decisions are settled and by whom. Be generous here; it is what keeps the audit cheap.>

## Load-bearing claims — verify these specifically
<The claims the remaining design rests on, each with the consequence of being false:
"<task X> exists only because <claim>. If that does not hold in the real data, say so loudly —
<task X> comes back out.">

## Your job — find what would make an executing agent fail
Not to praise the plan, not to restate it. Assume the reader is competent, has never seen this
repo, and will follow these documents literally.

1. **Could each remaining `Verify:` command fail?** Run them. Report any that pass today — a filter
   matching tests that already exist verifies nothing, and the task gets accepted unimplemented. A
   filter naming a class the task will create is correct; a command that passes regardless is not.
2. **Dependencies.** Does everything the remaining tasks name — endpoint, helper, table, fixture,
   file path — either exist now or get built by an earlier task? Name anything assumed and
   uncommissioned.
3. **Numbering.** Is every task number defined exactly once, across every document? Does anything
   still cite a number that resolves to superseded work?
4. **Accuracy.** Spot-check the load-bearing claims against the code and the real system. Report
   anything stated as fact that is not true.
5. **Contradictions.** Where do two documents disagree, or one still describe a superseded state?
6. **Red lines.** Does `CONVENTIONS.md` still forbid everything the *remaining* tasks could
   destroy? It was written before these phases existed. Name what a worker briefed only on that
   file could break.
7. **Ordering.** Does anything depend on work scheduled after it — in sequence, or in contract?
8. **The one thing most likely to be misread.** Be specific and opinionated.

## Bar for a finding
Name a concrete consequence: *an agent reading X would do Y, which breaks Z*. "Could be clearer" is
not a finding. Verify before reporting. Max 8, ranked, each BLOCKER (real damage or wasted work) or
MINOR.

## Do not
Do not modify any file — this is an audit. Do not write code. Do not fix what you find; report it.

## Report format
VERDICT: ready | ready-with-minors | not-ready
FINDINGS: <BLOCKER|MINOR> <file:section> — <what an agent would do, and what breaks> (max 8, ranked)
VERIFY COMMANDS: <one line per remaining task — command → does it go red on unimplemented work?>
VERIFIED: <one line per load-bearing claim checked, and whether it held>
SHAPE: <is the remaining plan still the right shape, given what the completed phases taught?>
MOST LIKELY MISREAD: <your answer to 8>
Under 30 lines. No code blocks.
```

### Handling the result

- **BLOCKER** → edit `PLAN.md` (or `CONVENTIONS.md`) before the next task runs; log it as a re-plan.
- **MINOR** → ledger follow-ups. Don't stop the phase.
- **Re-audit after fixing blockers** → `SendMessage` the same agent rather than spawning a new one; it still holds the documents and whatever harness it built, so confirming the fixes costs a fraction of the first pass. **Not optional when the fix was text surgery on a long file** — the round that repairs blockers is the round most likely to introduce one.
- **not-ready twice on the same axis** → the shape is the problem, not the details. Escalate; do not audit a third time.

---

## Recon agents

Use `Explore` when you need to *know* something, not change it.

- **Just-in-time.** At the start of the phase that needs it — never more than one phase ahead. Intel about code that doesn't exist yet is stale before you use it.
- One question per agent, disjoint from the others. Overlapping questions produce redundant reading and answers you have to reconcile.
- Ask for **≤15 lines with file paths**. Coordinates, not content.
- **An asserted capability or behaviour is not evidence — yours included.** Confirm it against `--help`, the code, or a probe before it reaches a brief or a doc, or mark it `unverified` so the worker checks it rather than builds on it.
- Max 4 at once, one round. If the answers raise new questions, that's usually the evidence budget telling you to decide and move on.
- **Run recon anyway, even when the plan names files**, if the plan predates commits on this branch or targets a system you don't control. Stale `file:line` references are the most confident-looking wrong information a plan can contain.

Good recon questions are answerable and bounded:
- "Where is tenant filtering applied for `X` entities, and what's the canonical helper? Paths + the helper's signature."
- "Does this repo have an existing pattern for background job registration? One example path and how it's wired."

Bad recon questions return essays: *"Tell me about the auth system."* → 200 lines, and you still won't know what you needed.

---

## Test policy

**Write tests for:**
- Logic with branches, edge cases, and boundary conditions.
- Contracts other code depends on — the thing that breaks silently when someone changes it.
- Every bug being fixed: **regression test first, confirmed red, then fix.** Non-negotiable.
- Anything the plan explicitly asked to be test-driven.

**Do not write tests for:**
- Framework/DI wiring, config registration, route attributes.
- DTO shapes, getters, trivial pass-throughs, generated code.
- Anything requiring a live third-party service or real network.
- UI rendering details that will churn next week.

**Don't build the harness.** If an area has no test infrastructure, do not have a worker invent one mid-task — that's its own task, planned deliberately, or explicitly out of scope. Where the repo has an established testing style, follow it; two competing styles is worse than one imperfect one.

**A test that can't fail is worse than no test.** A red-check proves only that the test fails against the one bug you fixed — not that the fixture can discriminate the rule. If a proposed test would pass against a deliberately broken implementation, drop it.

---

## Handling review output

- **BLOCKER** → rework task, fresh worker, narrow brief quoting the finding.
- **MINOR** → ledger follow-ups. Do not stop the phase for minors. Batch them into one cleanup task at the end of the phase if there are enough to be worth an agent — one brief, not one agent per fix.
- **Re-verifying the fix** → `SendMessage` the original reviewer, don't spawn a new one.
- **Reviewer and worker disagree** → you decide, using the reversibility test. Log the call and why. Do not run a third agent to break the tie; that's the paralysis loop.
- **Reviewer returns nothing** → a legitimate, common result. Accept and move on.
