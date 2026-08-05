# Delegation — briefs, report contract, recon

## Choosing the agent

| Need | Agent | Parallel? |
|---|---|---|
| Implement a task (writes files) | `general-purpose` | **Never.** One at a time. |
| Answer a scoped question about the codebase | `Explore` | Yes, up to 4, disjoint questions |
| Review finished work | `general-purpose` (review brief) | One at a time |
| Design/architecture options for a hard task | `Plan` | Rarely; usually you decide |

Always `run_in_background: false` for writers and reviewers — you need the result before the next step.

## The worker brief

Copy this shape. A brief missing done-criteria or a verification command is a defect; fix it before spawning.

```markdown
## Context
<2–4 sentences. The high-level goal of the undertaking, and where this task sits
in it. Workers who understand the destination make better local calls than
workers following instructions. Include what previous tasks already delivered.>

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
Conventions: read <CLAUDE.md / AGENTS.md / instruction file> before writing.
Precedent to follow: <existing file that does the analogous thing>

## Done when
- [ ] <criterion — specific and checkable>
- [ ] <criterion>
- [ ] <tests, if the test policy calls for them — say which behaviors>

## Verify with
`<exact command>` — must be green before you report done. Run it yourself.

## How to think
Think critically about the approach, but with a budget: if a design question is
still open after ~3 targeted searches or ~5 file reads, take the
cheapest-to-reverse option, note it under ASSUMPTIONS, and keep moving. Do not
read broadly "to be safe". If you find the task's premise is wrong, stop and
report that instead of forcing it through — a correct "this doesn't work
because X" is worth more than a plausible-looking implementation.

## Do not
- Do not commit or push. Leave changes in the working tree; the orchestrator commits.
- Do not paste code, diffs, or file contents into your report.
- Do not report done if verification is red. Report `blocked` or `partial` with the failure.

## Report back in exactly this format
STATUS: done | partial | blocked
FILES: <path — one line on what changed> (one per line)
VERIFY: <command> → <actual result, e.g. "12 passed" / "2 failed: <names>">
DEVIATIONS: <what you did differently from this brief, and why> | none
ASSUMPTIONS: <decisions taken without conclusive evidence> | none
RISKS: <what might bite later> | none
NOT DONE: <anything in scope you did not finish, and why> | nothing

Keep it under 25 lines. Prose beyond this format is noise.
```

### Why a fresh worker for rework

When a task fails verification, spawn a **new** agent with a narrow brief naming exactly what's wrong — don't continue the original via `SendMessage`. The original's context is anchored on what it *intended* to write; a fresh context reads the code as it actually is. Continue the original only for genuinely additive work (e.g. "also handle the null case"), never for a correctness failure.

## Recon agents

Use `Explore` when you need to *know* something, not change it. Rules:

- One question per agent, disjoint from the others. Overlapping questions produce redundant reading and duplicate answers you have to reconcile.
- Ask for **≤15 lines with file paths**. You want coordinates, not content.
- Max 4 at once, one round. If the answers raise new questions, that's usually the evidence budget telling you to decide and move on.
- Skip recon entirely when the plan already names the files.

Good recon questions are answerable and bounded:
- "Where is tenant filtering applied for `X` entities, and what's the canonical helper? Paths + the helper's signature."
- "Does this repo have an existing pattern for background job registration? Show me one example path and how it's wired."

Bad recon questions are open-ended and return essays:
- "Tell me about the auth system." → you'll get 200 lines and still not know what you needed.
