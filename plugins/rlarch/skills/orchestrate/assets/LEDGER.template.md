# Ledger — <undertaking name>

> Written for a fresh orchestrator with zero context: if this session were killed
> right now, this file must be enough to continue. The header block below is the
> recovery payload — keep it current. The task log is a table; write prose only
> when a row can't carry it. If the commit message already says it, don't repeat it.

## Status
- **Phase:** <n> of <N> — <name>
- **Current task:** <id> — <state>
- **Next action:** <the literal next thing to do>
- **Branch:** <branch> · **Last commit:** <sha>
- **Mode:** full | light
- **Last plan audit:** <phase boundary, or "none"> — <verdict> · **Next due:** <the cadence, so a fresh orchestrator can compute it>

## Open assumptions
| # | Assumption | Evidence level | Reversal cost | Revisit when |
|---|---|---|---|---|
| 1 | | | | |

## Deferred follow-ups
- [ ] <item> — from task <id>

## Orchestrator edits
<Zero-behaviour changes made directly (comments, docs), with task id. Empty is the good case.>

---

## Task log

| Task | Verdict | Commit | Verify | Note |
|---|---|---|---|---|
| 1.1 <name> | ACCEPTED | <sha> | `<cmd>` <result> | — |
| 1.2 <name> | REWORKED → ACCEPTED | <sha> | `<cmd>` <result> | see below |

Verdicts: ACCEPTED · REWORKED · RE-PLANNED · ESCALATED · SKIPPED
`Note` carries the one thing worth knowing — a deviation, a skipped review and why, a red-check that mattered. Otherwise `—`.

---

## Exceptions
<Only for what a table row can't carry. Most tasks have no entry here.>

### <task id> — <deviation | assumption | failure>
<What it was, what you decided, and what it cost or would cost to reverse.
2–5 lines. If it changed the plan, say what changed.>

### Plan audit — after phase <n>, over phases <n+1..N>
Verdict: <ready | ready-with-minors | not-ready>
Blockers: <one line each, and what changed in PLAN.md / CONVENTIONS.md> | clean
Re-audited after fixes: <yes — verdict | no, the fixes were not text surgery>

### Phase <n> gate — <PASSED | FAILED | SKIPPED — no seam>
Seams checked: <one line each>
Findings: <…> | clean
Last commit in phase: <sha>
Re-gated after fixes: <yes — sha | no, fixes didn't touch the seam>

### Re-plan — <marker>
<What reality turned out to be, and what changed in PLAN.md. One paragraph.>
