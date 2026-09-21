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

Two columns, because a budget with one measurement is a number and a budget with two is a trend.
**Before** is 2026-09-20, with the write half in place and before the acceptance runs; **after** is
2026-09-21, with U5's fixes in.

| | Before | After | Budget | |
|---|---|---|---|---|
| `ServerInstructions` | 35 lines | **35 lines** | ≤40 lines | doc 05 verbatim, now asserted against the document itself |
| `tools/list` — worker | 1,881 | **1,890** (6 tools) | 2,200 tokens | |
| `tools/list` — orchestrator | 5,538 | **5,557** (18 tools) | 6,400 tokens | ceiling is measured+15%, a **ratchet detector, not a target** — untouched |
| `work_brief` on the `sample-run` fixture | 574 | **583** (2,331 chars, 34 lines) | ≤900 tokens | build-failing test |
| `work_brief` on real `entity-store` | **581 tokens** | not re-run | — | one-off; against **473,988 chars / ~118,500 tokens** of `LEDGER.md` = **204×** |

**+19 orchestrator tokens, +9 worker, +9 on the brief.** That is one reworded `Next:` line
(`work_verify` now names `work_next`, which is what put every tool inside two hops of an orientation
point), one reworded argument on `work_get`, and one extra line per item in `work_get` carrying the
`work://item/<ref>/brief` URI the agent had to compose for itself. **The ceilings were not moved.**

#### The overlap nobody had measured

doc 04 §1 forbids `ServerInstructions` restating the schema. One level down, the same question: both
the tool description and the `inputSchema` property descriptions ship in `tools/list`, so any overlap
is paid for twice. **Measured over 18 tools: mean 13%, median 11%, six tools at 0%, one outlier at
47%.**

The outlier is `work_verify`, and it is not duplication: its description exists to say that `run_by`
comes from the token and not from the argument, which cannot be said without naming the argument.

**So 5,557 tokens is the price of the channel, not slack in it.** There is nothing to cut that is not
doc 04 §2's third question, and the measurement is recorded as a finding rather than acted on.

### Defects found, and the channel each belongs to

All five carried forward from Run A-0 and Run A. **Every one of them passed its own unit tests on the
day it was found** — which is the whole argument for the transcript.

| # | What the agent saw | Channel at fault | Fix | Regression test |
|---|---|---|---|---|
| **A-1** | the ambiguity refusal named `document-store/3.1` to an agent working in `entity-store` — callable, confident, wrong | the refusal | every candidate is listed, the caller's own run first; `BoardReader.Prefer` | `An_ambiguous_ref_lists_every_candidate_and_puts_the_callers_own_run_first` |
| **A-2** | `work://item/3.1/brief` answered *"this server holds no work yet"* about a store holding 267 tasks | the resource, which had its own copy of the not-found branch | one `ScopeExplain` for every surface that resolves a ref; only a genuinely empty workspace gets onboarding | `An_ambiguous_item_uri_refuses_and_never_claims_the_server_is_empty` |
| **A-3** | `work_brief` on a task rendered `RUN 3.1 · project phase-3` | the brief renderer, and the header query behind it | the scope labels itself by its own kind; the run and the project are looked up by kind, not taken from the parent row | `A_brief_on_a_task_labels_it_a_task_and_names_the_real_run_and_project` |
| **A-4** | the composed `work://item/entity-store/3.1/brief` 404'd | the URI template, and `work_get` for not printing the brief URI at all | `{+ref}` (RFC 6570 reserved expansion) matches a qualified ref; `work_get` prints both projections' full URIs | `A_qualified_item_uri_reads_back_in_both_its_projections` |
| **F-L6** | no way to create a database without pointing the corpus importer at markdown | the API, which owns this database and did not create it | `SchemaOwner` applies the migrations at startup; `WorkState:Migrate=false` opts out | `The_api_creates_its_own_schema_so_a_first_run_needs_no_importer` |

**Two of the four came from the same root cause**, and it is worth naming: A-2 and the read tools'
scope refusal were two copies of "what do I say when a ref does not resolve", and only one of them
had been thought about. The copy nobody looked at is the one that told an agent the store was empty.

### The channel audit, now that it is a test rather than a checklist

doc 04 §2 names three properties of the `Next:` graph. All three are assertions over the live
`tools/list` payload, so they cover any tool added later:

| Property | Where | Result |
|---|---|---|
| every `Next:` names a tool that exists | `Every_Next_line_names_a_tool_that_exists` | pass — no shipped description names `work_patch` |
| every path reaches `work_next` or `work_brief` within two hops | `Every_tool_reaches_work_next_or_work_brief_within_two_hops` | **failed on first run**: `work_report → work_verify → work_status → work_next` was three. Fixed in the channel — `work_verify` now names `work_next` after the close. `work_note` is the one declared terminal, per doc 04 §2 |
| `work_report → work_verify` in those words | `work_report_points_at_work_verify_in_those_words` | pass |
| every refusal names a callable fix | `Every_refusal_the_server_can_emit_names_a_callable_fix` | pass over **25** refusal paths, read and write |
| `ServerInstructions` is doc 05 verbatim | `ServerInstructions_are_doc_05_verbatim` | pass — compared against the document, not eyeballed |

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

### The same check, scripted — so it survives the agent that passed it

A transcript proves a thing happened once. Check 2 also has a mechanical half, and it is the half
that keeps holding after everyone has moved on:
`ErrorChannelTests.A_refused_close_explains_the_remedy_before_and_more_than_the_override`.

It closes an unverified task and asserts four things about the refusal that comes back:

- it **is** refused, and the refusal names every unmet condition with a `→` that is a callable line;
- the **override is named** — doc 06 §6, an escape hatch that is hidden gets worked around;
- the **first remedy appears before `force:true`** in the text, and there are **more remedies than
  overrides**. This is the sentence from doc 04 check 2 turned into an assertion: *"if the refusal is
  explaining the escape hatch better than the remedy, it is the refusal that is wrong."*
- the honest route is spelled out by name: `work_verify(` and `work_update(` both appear.

What it deliberately does **not** assert is what an agent chose to do, because no test can. The
observed run above is the evidence for that half, and the two are worth different things: the
transcript says *an agent that had never heard of the guard chose the work over the escape hatch*;
the test says *the refusal it read cannot silently stop saying so.*

A ratio rather than a wording check, for the reason the rest of this file keeps returning to: a
wording check passes the moment someone writes the wording, whether or not it still reads that way.

