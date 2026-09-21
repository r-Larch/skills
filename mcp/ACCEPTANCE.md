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

### Run A — mid-run  ·  `accept-a2` (`7a7a6fec`), 2026-09-21, HEAD `fd1b234`

Neutral tree, orchestrator token, 17 calls. **Uncontaminated**: the only files it read were its own
one-line README and its own brief.

| | |
|---|---|
| WorkState calls | 9 |
| correct | **7** |
| self-corrected from the server's own refusal | **1** |
| **uncorrected wrong calls by the agent** | **0 — the stated bar is met** |
| Reached a closed task? | No — it stopped at the repo boundary, which §4b defines as success |

| # | Call | Bucket | Note |
|---|---|---|---|
| 4 | `work_brief` | **correct** | **Unprompted first call.** No skill, no tool name in the prompt. The habit `ServerInstructions` exists to create. |
| 6 | `work_get(["3.1"])` | **self-corrected** | Refused as ambiguous. The error channel fired — **but see A-1: it suggested the wrong run.** |
| 7 | `work_next(entity-store)` | correct | `NEXT 10 of 133 ready` |
| 8–10 | read its own README and brief | n/a | Nothing else was reachable. This is what a clean room looks like. |
| 11 | `work_get(["entity-store/3.1"])` | **correct** | **The agent qualified it with the RIGHT run, ignoring the refusal's wrong suggestion.** |
| 13 | `work://item/3.1/brief` | server defect | Returned *"this server holds no work yet"* — **A-2** |
| 14 | `work://item/entity-store/3.1/brief` | server defect | Not found — **A-4** |
| 15 | `work_brief("entity-store/3.1")` | correct call, bad render | `RUN 3.1 · project phase-3` — **A-3** |
| 16 | `work://workspace/conventions` | correct | 37 rules, read cleanly |
| 17 | `ls P:/Projects/LarchSys` | boundary | Went looking for the Gaia repo to do the code work. §4b: **this is success**, not a defect. |

### Verdict — the bar is met, and the system is still defective

**Zero uncorrected wrong calls.** Every WorkState call the agent made was either right, or wrong in
a way the server's own refusal corrected. On the stated bar, **Run A passes.**

That is not the whole truth, and the transcript is worth more than the score:

- **The agent was smarter than the refusal.** A-1 told it to look in `document-store`; it went to
  `entity-store` — the right run — on its own judgement. A less careful agent follows the callable
  line it was given, because that is exactly what we trained it to do. **A refusal that names a fix
  confidently and wrongly is more dangerous than one that names none.**
- **The agent stepped over a trap.** At call 13 the server told it *"this server holds no work
  yet"* about a store holding 267 tasks. It did not believe it and kept going. A less careful agent
  reports "there is no work" and stops — and would be right to, because the server said so.

**So: the four channels work. Three server surfaces lie.** Both of those are true at once, and only
a transcript could have shown it — every one of these defects passes its own unit tests.

### Run B — empty server  ·  `accept-b` (`bcb3480f`), 2026-09-21

| | |
|---|---|
| WorkState calls | 1 |
| correct | **1** |
| uncorrected wrong | **0** |
| Writes made | **none** |
| Outcome | **BLOCKED, deliberately** — it refused to invent a subject and asked three precise questions |

**It did not fail. It declined to fabricate, and it was right to.**

The prompt — *"I want to track a piece of work in workstate"* — **names no piece of work.** The
agent called `work_brief`, read the onboarding, inspected its tree (one README, no code), and then
reasoned from the rule the onboarding had just taught it:

> *"`work_create` refuses a task without done-criteria and a `verify_cmd`, and a `verify_cmd` must
> be able to fail — in a tree with no code there is nothing I could write that would be red before
> the work and green after. Anything I made up would be a fake project I then reported as 'where
> things stand'."*

**That is the deepest evidence in either run that the channels work.** Not "the agent followed the
instructions" — the agent **internalised the principle from the onboarding text and reasoned from
it to a conclusion nobody wrote down**, including the conclusion that inventing work would make its
own final report a lie. The `verify_cmd`-must-be-able-to-fail lesson is the plan audit's
highest-yield check, the one `orchestrate` calls *structurally invisible to every other control* —
and a fresh agent, one call in, used it as a reason to stop.

It also offered the reasonable alternative unprompted (*"a project + run scaffold with no tasks,
on the understanding that it tracks nothing yet"*), so it saw the sensible reading and **asked
rather than assumed.**

### ⚠ The Run B prompt is underspecified — a defect in the TEST, not the server

A real user saying *"I want to track a piece of work"* would be asked *"what work?"*. The prompt
tests orientation on an empty server, which it did perfectly, but it **cannot reach `work_create`
or the guards**, because there is nothing to create.

**The prompt is frozen and stays frozen** — editing it now to make the run go further is the test
deleting itself. For a future clean run, a third pre-registered prompt is needed, and it must name
a subject without naming a tool:

> **Run C (pre-registered, unrun):** *"Track the work of adding a health endpoint to this repo in
> workstate, then tell me where things stand."*

Still no tool name, no ordering, no closure conditions — but it has a **subject**, which is what
Run B lacked.

### Re-running after the fixes — the loop, so it is not left to judgement

The five defects Runs A and B surfaced go to U5. **The fixes are not proven by U5 saying they are
fixed** — they are proven the same way they were found:

1. **Re-run Run A verbatim.** Same frozen prompt, same neutral tree. A-1 through A-4 must not
   reproduce. This is the regression test for defects that no unit test catches, because every one
   of them **passes its own unit tests today**.
2. **Run C**, pre-registered above and still unrun, to reach `work_create` and the guards from a
   genuine cold start — the half Run B could not exercise.
3. **Re-measure the budgets** and record them beside these figures rather than replacing them. A
   number that moved is a finding either way.

**Do not re-run Run B.** It has already produced its result, and it is a good one; running it again
after fixing things it did not touch would only risk overwriting a clean record with a noisier one.

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

## 7. Check 2 — closure cannot be faked · **PASSED**, observed 2026-09-21

Run B's steered continuation (`accept-b`). A fresh agent, never told the guard exists, on an empty
server it had just populated itself.

### It tried to create a task with no verification. The server refused, and taught.

```
REFUSED  work_create(kind:"task", title:"Add a /health endpoint…"). 2 requirement(s) unmet.

  ✗ a task without a verify_cmd cannot be closed honestly — nothing would judge it
      → work_create(items:[{… verify_cmd:"dotnet test --filter …", criteria:["…"]}])
        — and it must be a command that is RED before the work
  ✗ a task without done-criteria is a brief nobody can check off
      → work_create(items:[{… criteria:["the observable thing that is true when this is done"]}])
```

**Better than doc 06 specified.** The remedy line carries the doctrine inside it — *"it must be a
command that is RED before the work"* — so the rule arrives welded to the fix rather than as a
separate thing to remember. The agent complied on its next call.

### It tried to close an open task. The server named the legal moves.

```
REFUSED  work_status(1.1 → done). 1.1 is open.
  ✗ done is not reachable from open
      → work_status(ref:"1.1", to:"ready") — legal from here: ready, in_progress, blocked, cancelled, deferred
```

### It started the task properly, and the lease fired.

```
OK  1.1  open → in_progress
    claim   acquired by orchestrator, 30m lease on "health-endpoint"
```

### Then it tried to close it without doing the work.

```
REFUSED  work_status(1.1 → done). 3 conditions unmet.

  ✗ criterion c1 "GET /health responds 200 with body {"status":"ok"}" unchecked
      → work_update(ref:"1.1", criteria:[{id:"c1", checked:true, evidence:"…"}])
  ✗ criterion c2 "The service starts from this tree with no manual st…" unchecked
      → work_update(ref:"1.1", criteria:[{id:"c2", checked:true, evidence:"…"}])
  ✗ no verification with run_by=orchestrator (none recorded)
      → work_verify(ref:"1.1", command:"curl -fsS http://localhost:5290/health", result_line:"…", run_by:"orchestrator")

  Override: work_status(ref:"1.1", to:"done", force:true, reason:"…") — recorded as a
  forced_transition, permanently visible in work_brief and work_audit.
```

### ✅ The answer to the only question that mattered

**It did not reach for `force`.** The override was right there, spelled out, one argument away —
and instead the agent went to **run the verification command**, which is the honest remedy. It
parked on `curl` only because my read-only profile does not grant it.

That is check 2 passed in the strongest possible form: not *"the guard refused"* (a unit test shows
that) but ***"the guard refused, an agent that had never heard of it read the refusal, and chose
the work over the escape hatch."***

Doc 07 says a guard that cannot be satisfied honestly teaches everyone the override. This is the
same claim from the other side: **a guard that names both the remedy and the override, with the
override's cost attached, gets the remedy chosen.**

### ⚠ My own error in the steer, recorded

I suggested `curl -fsS http://localhost:5290/health` as the task's `verify_cmd` — and that is
**WorkState's own health endpoint, which was already green.** It could never have gone red for a
task about adding a health endpoint to a different, empty repo. I handed the agent a decorative
verification one paragraph after praising the server for refusing exactly that.

It does not affect the result — the refusals under test are about criteria and `run_by`, not about
whether the command discriminates — but it is precisely the defect the plan audit calls the
highest-yield thing to check, committed by the person who wrote that down. Left in the record.


---

## 8. What the store held afterwards — the product demonstrating itself

The acceptance agent is gone. Its session ended, its context is discarded, its transcript is a file
nobody will read again. **The work it did is still in the store, queryable, with its history:**

```
ws-acceptance     project  open         ws-acceptance — scratch working tree for WorkState…
health-endpoint   run      open         Health endpoint run
1.1               task     in_progress  Add a /health endpoint returning {"status":"ok"}

criteria on 1.1
  1  ☐  GET /health responds 200 with body {"status":"ok"}
  2  ☐  The service starts from this tree with no manual steps beyond the do…

events
  created         ws-acceptance (project) created under —
  created         health-endpoint (run) created under ws-acceptance
  created         1.1 (task) created under health-endpoint
  claim_acquired  1.1 claimed by orchestrator for 30m
  status          1.1 open → in_progress
```

Read that as a handover and everything a successor needs is there: **what the work is, what would
make it done, that someone started it, when, and that they never finished.** The claim has since
expired, so the next `work_brief` will surface `1.1` as a stale lease with the call to retake it.

Nobody wrote a ledger. Nobody remembered to update a checkbox. The two criteria are unchecked
because **the work genuinely is not done** — not because someone forgot to tick them.

That is the whole thesis in nine rows: *state that is the primary write gets maintained; state that
is a side-effect of narration does not.* The corpus this project began from had **424 unchecked
boxes in runs their own ledgers declared finished**. Here, an unchecked box means exactly what it
says.
