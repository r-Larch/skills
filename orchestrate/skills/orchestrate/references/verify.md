# Verification — the review ladder, reviewer briefs, test policy

Verification is what makes delegation safe. Without it you're not orchestrating, you're forwarding.

## The ladder — climb only as high as the task warrants

**Rung 0 — always, no exceptions: run the command yourself.**
Build / typecheck / test, plus `git status` and `git diff --stat`. Worker self-reports of "tests pass" are the most common false statement in this system, and catching it costs you ten seconds. Also confirm the changed-file list matches what the worker reported — unreported files are a finding.

**Rung 1 — orchestrator self-review.** Sufficient when *all* hold: ≤3 files, mechanical change, rung 0 green, the verification genuinely covers the behavior, and the report shows no deviations or assumptions. Spot-check ≤50 lines of the most important hunk and accept.

**Rung 2 — review subagent.** Required when **any** of these is true:
- touches auth, tenancy/isolation, permissions, secrets, or money;
- schema change, data migration, or anything deleting data;
- changes a public/wire contract (API shape, event, widget/embed surface);
- >5 files, or a new subsystem/abstraction;
- the worker reported a DEVIATION or an ASSUMPTION;
- rung 0 was green but you don't believe the tests actually exercise the change.

**Rung 3 — phase gate.** After every phase, always. Reviews the phase as a whole against the goal paragraph.

Don't climb higher than the rules require. A second reviewer on a green three-file change is theater that costs context.

## Review brief

```markdown
## What to review
The changes in <files / `git diff` on branch <b> since <SHA>>.

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
the failure is reachable. Report at most 5, ranked, and mark each BLOCKER or MINOR.

## Also answer
- Does this actually serve the goal, or does it merely satisfy the letter of the criteria?
- Is anything in the done-criteria unmet or only apparently met?
- Is there a missing test for a behavior that meets the test policy bar?

## Report format
VERDICT: pass | pass-with-minors | fail
FINDINGS: <BLOCKER|MINOR> <file:line> — <failure scenario> (one per line, max 5)
GOAL FIT: <one or two sentences>
No code blocks. Under 20 lines.
```

## Test policy

**Write tests for:**
- Logic with branches, edge cases, and boundary conditions.
- Contracts other code depends on — the thing that breaks silently when someone changes it.
- Every bug being fixed: regression test first, watch it fail, then fix. Non-negotiable.
- Anything the plan explicitly asked to be test-driven.

**Do not write tests for:**
- Framework/DI wiring, config registration, route attributes.
- DTO shapes, getters, trivial pass-throughs, generated code.
- Anything requiring a live third-party service or real network.
- UI rendering details that will churn next week.

**Don't build the harness.** If an area has no test infrastructure, do not have a worker invent one mid-task — that's its own task, planned deliberately, or explicitly out of scope. Where the repo has an established testing style, follow it; two competing test styles is worse than one imperfect one.

**A test that can't fail is worse than no test** — it costs maintenance and buys false confidence. If a proposed test would pass against a deliberately broken implementation, drop it.

## Handling review output

- **BLOCKER** → rework task, fresh worker, narrow brief quoting the finding.
- **MINOR** → ledger follow-ups. Do not stop the phase for minors. Batch them into a cleanup task at the end of the phase only if there are enough to be worth an agent.
- **Reviewer and worker disagree** → you decide, using the reversibility test. Log the call and why. Do not run a third agent to break the tie; that's the paralysis loop.
- **Reviewer returns nothing** → that's a legitimate, common result. Accept and move on.
