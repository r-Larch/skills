# Acceptance — can a coding agent work this out without being taught?

> The goal, in the owner's words: **"a working MCP that is self-explaining in how to use it to a
> coding agent."** Not documented. *Self-explaining.* A surface an agent has to be taught by a skill
> file has failed.
>
> Prompts pre-registered `ea17ed8`, **before the read side shipped and before any tool name was
> final** — so the ordering is checkable, not asserted. `docs/workstate/04-self-explaining.md`.

---

## 1. Seed the two states

**Mid-run** — the whole Gaia corpus, 8 runs, ~270 tasks, including `entity-store` whose markdown
ledger is 6,011 lines:

```
docker compose -f mcp/compose.dev.yml up -d
dotnet run --project mcp/tools/WorkState.Import -- --migrate --connection "Host=127.0.0.1;Port=55433;Database=workstate;Username=workstate;Password=workstate" --root "P:/Projects/LarchSys/Gaia/.claude/orchestration"
```

**Empty** — same database, no import. Bring it up clean, or drop the project row.

Start the server (Development profile reads the 55433 connection and the three role tokens):

```
dotnet run --project mcp/WorkState.Api
```

## 2. Register it

```
claude mcp add --transport http workstate http://localhost:5290/mcp --header "Authorization: Bearer dev-orchestrator-token"
```

Remove with `claude mcp remove workstate` afterwards.

## 3. The prompts — frozen, and the only thing the agent is told about the subject

**Run A — mid-run:**

> Use workstate to pick up the next piece of work on the imported run and take it as far as you can.

**Run B — empty server:**

> I want to track a piece of work in workstate. Set it up and tell me where things stand.

**Forbidden in the prompt** (doc 04): any tool name, the order of operations, the closure
conditions, the existence of an attention block, the word "ref". *If the prompt would still teach a
server-less agent what to do, it is contaminated.*

## 4. The one known contaminant, stated rather than hidden

The agent is launched by `nested-claude`'s `Start-NestedAgent.ps1`, which **prepends ~20 lines** to
every brief: who its controller is, the `READY` / `BLOCKED` / `DONE` protocol, and *"if you hit a
permission prompt you cannot clear, send BLOCKED naming the exact operation."*

**It says nothing about WorkState, MCP, tools, or work tracking of any kind.** So it shapes how the
agent *reports*, not what it does about the subject under test. Recorded here because a test whose
contaminants are listed can be judged; one whose contaminants are unmentioned cannot.

Its one useful side effect: a tool the agent cannot call produces a `BLOCKED` naming it, instead of
a silent park — so an allow-list gap shows up as data rather than as a hang.

## 4a. The room is part of the test — learned by getting it wrong

**Run A-0 was void because the agent read this file.** Call 6 of 16 was
`Read mcp/ACCEPTANCE.md` — the purpose, both frozen prompts, the scoring buckets. It was pointed at
the `Skills` checkout, which holds this runbook and all eight design documents.

The prompt discipline was fine. **The room was not.** And the agent was *right* to read them: told
to "use workstate", standing in a repo full of workstate documentation, reading the documentation is
what a competent agent does.

> **A frozen prompt does not protect a test whose environment leaks.**

**So the acceptance runs from a neutral working tree**: `P:\Projects\LarchSys\ws-acceptance` —
`git init`, one README, nothing else. No `mcp/`, no `docs/workstate/`, no repo that the imported
runs describe. The agent can reach **the server and nothing else**, which is the only configuration
in which the transcript means what it claims to mean.

Two practical notes for whoever re-runs this:

- **MCP registration is per-project.** Registering `workstate` in the `Skills` checkout does not
  register it in the neutral tree; run `claude mcp add` from the neutral tree itself.
- **Check what is reachable before launching, not after.** `find . -name "*.md"` in the tree you are
  about to point an agent at costs one command and is the whole defence.

## 4b. What the acceptance agent may do, and why it is narrow

Granted: **read-only shell, `Read`/`Glob`/`Grep`, and all 18 `mcp__workstate__*` tools.**
Nothing else. No `Edit`, no `Write`, no repo access to the projects the imported runs describe.

Two reasons, and the second is the one that matters:

1. **Grant all 18, not a subset.** The role is decided by the **token**, so `tools/list` already
   narrows correctly. An allow-list gap would produce a `BLOCKED` that looks like a tool-surface
   defect and is not — a false finding is worse than no finding.
2. **The subject under test is whether the agent can drive WorkState**, not whether it can do the
   underlying engineering. Run A says *"take it as far as you can"*; an agent that reaches the point
   of needing to edit a Gaia source file and says so has **succeeded** at the thing being measured.
   Score the WorkState calls; the code work is out of scope and its absence is not a defect.

## 5. How a call is scored

Every call, in order, into one of three buckets:

| Bucket | Meaning |
|---|---|
| **correct** | advanced the work |
| **self-corrected** | wrong, and the **server's own refusal or error** put it right on the next call — **this is a PASS for the error channel**, not a failure. It is doc 04 §3 working: the cheapest teacher in the system, arriving exactly when it is relevant |
| **uncorrected** | wrong, and nothing in the system recovered it |

**The bar is zero in the third bucket.** Each one is a defect against the specific channel that
should have prevented it — `ServerInstructions`, a tool description, a refusal, or `work_brief`'s
output — and it is fixed **there**. Never by adding an explanation to the prompt.

**No steering.** The controller does not answer, correct or hint during a run. If the agent parks or
gives up, that is the result.

## 6. Results

### Run A — mid-run

| | |
|---|---|
| Date / commit | |
| Calls: correct / self-corrected / uncorrected | |
| Reached a closed task? | |
| Verdict | |

Call-by-call:

| # | Call | Bucket | Note |
|---|---|---|---|

### Run B — empty server

| | |
|---|---|
| Date / commit | |
| Calls: correct / self-corrected / uncorrected | |
| Created a project and oriented? | |
| Verdict | |

Call-by-call:

| # | Call | Bucket | Note |
|---|---|---|---|

### Budgets

| | Measured | Budget | |
|---|---|---|---|
| `ServerInstructions` | | ≤40 lines | |
| `tools/list` — worker | | 2,200 tokens | |
| `tools/list` — orchestrator | | 6,400 tokens | ceiling is measured+15%, a **ratchet detector, not a target** |
| `work_brief` on a fixture | | ≤900 tokens | build-failing test |
| `work_brief` on real `entity-store` | **581 tokens** | — | one-off; against **473,988 chars / ~118,500 tokens** of `LEDGER.md` = **204×** |

### Defects found, and the channel each belongs to

| # | What the agent did | Channel at fault | Fix |
|---|---|---|---|

---

## 7. Check 2 — closure cannot be faked

Attempt to close an unverified task. The refusal must be **sufficient on its own** for the agent to
recover on the next call, with no second hint.

**Watch for the agent reaching for `force: true` instead of the remedy.** If it does, the refusal is
explaining the escape hatch better than the fix — and **the refusal is what is wrong**, not the
agent.
