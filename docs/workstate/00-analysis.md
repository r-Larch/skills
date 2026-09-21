# 00 — What the skills actually persist, and why files are the wrong store

> Evidence base: `orchestrate`, `nested-claude` and `controller` SKILL.md + references, plus **181
> markdown files / 55,523 lines** of real run artifacts across 25 runs in
> `Nomos`, `Nomos.Word`, `Nomos.Spiders` and `Gaia`.

---

## 1. The measurements that decide the design

| Measurement | Value | What it means |
|---|---|---|
| Largest single `LEDGER.md` | **6,011 lines** (`Gaia/entity-store`), 200+ headings | No agent reads this. The skill's own plan-audit brief says: *"do not read cover to cover. Search it for specific claims only."* The store has outgrown its access method, and the skill already knows it. |
| Ledgers ≥ 1,000 lines | 5 of 25 runs | Not an outlier — the steady state of a long run. |
| Checkbox drift | **424 unchecked boxes vs 78 checked across all 181 files — 84.5% of every checkbox ever written is still open**, and **17 of 25 runs have zero `[x]`**, including runs their ledger declares finished | `redesign` says *"Phase 4 of 4 — complete. All 9 tasks committed, 11 commits"* while `TASKS.md` shows **10 open / 0 done**. The truth lives in prose; the structured field is decoration. **This is the single strongest statement of the problem.** |
| `SUPERSEDE*` markers | 96 | Markdown cannot retract, so supersession is narrated. |
| `CORRECTION` markers | 129 | Same. `entity-store` contains *"### CORRECTION to the suite-abort entry above — the leading hypothesis is DISCONFIRMED"* — a **new append** because editing 6,000 lines in place is riskier than appending. |
| `stale` | 237 | |
| ⚠ warnings | 1,241 | Attention is sprayed as emoji because there is no channel for it. |
| *"do not re-derive / rediscover / re-litigate"* | 36 | Hard-won knowledge with a **named future consumer**, written into a document and relying on someone remembering to re-read it. |

**The headline:** state that is a *side-effect of narration* is never maintained. State that is the
*primary write* is. Every one of those numbers is the same defect in a different costume.

---

## 2. Fifteen kinds of knowledge, one text blob

The artifacts look like "a plan and a log". They are not. They are fifteen distinct things with
fifteen distinct lifecycles, conflated into two files addressed only by heading text:

| # | Kind | Lifecycle | Where it lives today |
|---|---|---|---|
| 1 | **Goal paragraph** | immutable, one per run | `PLAN.md` § Goal |
| 2 | **Decomposition** (phases → tasks) | mutable by re-plan | `PLAN.md` / `TASKS.md` |
| 3 | **Binding constraints** (red lines, stack facts, commands) | append-mostly, *scoped*, grows per phase | `CONVENTIONS.md` |
| 4 | **Briefs** | immutable once issued, one per attempt | `brief-N.N.md`, or inline in the Agent call |
| 5 | **Reports** (STATUS/FILES/VERIFY/RED-CHECK/DEVIATIONS/ASSUMPTIONS/RISKS/NOT DONE) | immutable per attempt | pasted into the ledger, or lost |
| 6 | **Verification evidence** (command → actual result line, counts, median/range) | immutable | ledger table cell |
| 7 | **Verdicts** (ACCEPT/REWORK/RE-PLAN/ESCALATE/SKIP + commit sha) | immutable event | ledger table |
| 8 | **Assumptions** (open → confirmed / disproven) | **state machine** with evidence level, reversal cost, revisit-when | a table, closed by `~~strikethrough~~` |
| 9 | **Deferred follow-ups** | open → closed / folded into a task | a checkbox list nobody checks |
| 10 | **Findings** (review / gate / plan-audit, BLOCKER\|MINOR) | open → fixed / deferred / rejected | dissolved into prose |
| 11 | **Carry-forward notes** ("Carry into later briefs") | **addressed to a future task** | a prose section, delivered by the orchestrator's memory |
| 12 | **Incidents** (credential leak, unscoped DELETE, spend limit, machine migration) | immutable | ⚠ sections |
| 13 | **Environment facts** (DB port, creds location, what regenerates what) | mutable, **machine-scoped** — `entity-store` survived *two* machine migrations that invalidated all of them | ledger header |
| 14 | **Recon answers** | immutable but **perishable** — explicitly invalid more than one phase ahead | ledger § Recon |
| 15 | **Corrections / supersessions** | meta-layer | exists *only* because #1–14 cannot be retracted |

Kind 15 is pure overhead created by the storage medium. It disappears the moment items have identity.

---

## 3. Structure the skills already impose — and cannot enforce

The three skills are, read closely, **an unenforced schema**. Every rule below is a constraint a
database enforces for free and a markdown file cannot enforce at all:

| Skill rule | Enforcement today | Enforcement in a store |
|---|---|---|
| *"Is every task number defined exactly once?"* (plan audit check #3) | an audit agent costing **60–150k tokens** | `UNIQUE (ref_scope_id, ref)` — scoped per run, so parallel orchestrators cannot collide |
| *"Does everything the remaining tasks depend on either exist, or get built by an earlier task?"* (#2) | same audit | a foreign key + a cycle check |
| *"A superseded task is deleted, not annotated"* | prose discipline, violated 96 times | `status = superseded` + a `supersedes` edge |
| *"A brief that omits done-criteria or a verification command is a defect"* | the orchestrator remembering | `NOT NULL` / a refused create |
| *"Every accepted task ends in a commit"* | discipline | a required artifact on the `done` transition |
| *"Run the command yourself"* — worker green is not evidence | discipline | two `verification` rows with distinct `run_by`; the guard reads the orchestrator's |
| *"Writing agents run one at a time. Never two in parallel"* | a sentence | a claim lease over a scope |
| *"Workers never commit; the orchestrator commits"* | a sentence in every brief | a **scoped token whose tool list has no `work_status`** — see §6 |
| *"Log by exception… if the commit message already says it, don't repeat it"* | judgment, honoured ~never | typed rows have no room for restatement |

The plan audit's two highest-yield checks are **integer uniqueness and referential integrity**. Today
they cost up to 150k tokens per run and fire at most once per phase boundary. In Postgres they are
constraints that cannot be violated, checked on every write, for free.

---

## 4. How an agent actually reads and writes a 6,000-line ledger

The user asked for this specifically. Here is the honest inventory of what I reach for, and what each
costs.

### Reading

| Tool | Shape | Real use on a big ledger | Cost |
|---|---|---|---|
| `Read(file)` | whole file, `cat -n` | **never** — 6,011 lines ≈ 90k tokens, and the skill forbids it | prohibitive |
| `Read(file, offset, limit)` | line window | the header block (lines 1–120) — the recovery payload | cheap, but you must already know the window |
| `Grep(pattern, -n, -C)` | regex + context | **the actual access method**: find the one section that mentions `ContentHash` | cheap; blind to structure |
| `Grep(output_mode: "content", head_limit)` | capped hits | scanning for `⚠` or `DEFERRED` | cheap; returns *lines*, not *items* |
| `Bash: sed -n 'a,bp'` | line window | same as Read+offset, avoids the read-tracking requirement | cheap |
| `Bash: grep -c` | counts | "how many open assumptions" — **answers with a number that is often wrong**, because closure is `~~strikethrough~~` not a field | misleading |

**Conclusion:** the read pattern is already *"one fixed projection + targeted search"*. Nobody reads
the document. The store should therefore expose **a brief projection and a query API**, not a file.

### Writing

| Tool | Contract | Why it is used | Failure mode |
|---|---|---|---|
| `Bash: cat >> f <<'EOF'` | append | **the dominant write.** O(1), no read required, no anchor to get wrong | the file only ever grows; corrections become new appends (129 of them) |
| `Edit(file, old_string, new_string)` | **anchored replace, must match exactly once** | the only safe in-place edit | fails if the anchor is not unique or drifted by a byte; requires a prior `Read`/`Grep` to reproduce the anchor verbatim including whitespace |
| `Edit(..., replace_all: true)` | global | renames | catastrophic on a string that recurs |
| `Write(file, content)` | **full overwrite** | rewriting a header block | rewrites the whole file; **a read-then-write silently converts CRLF→LF** — this repo has a commit about exactly that (`65ffbf4`), where it changed a prompt's runtime text and was caught only by a snapshot test |
| `Bash: sed -i` | regex substitute | scripted fixes | same line-ending hazard, plus regex escaping in markdown |

`Edit` is worth studying because **it is the best of these and the MCP must beat it.** Its virtues:

1. **Anchored, not positional.** It survives content being inserted above it. This is the single
   reason it works on a large file at all.
2. **Uniqueness is optimistic concurrency + typo detection in one.** A drifted or ambiguous anchor
   fails *loudly* instead of writing to the wrong place.
3. **Read-before-edit is enforced by the harness.** You cannot patch what you have not seen.

Its costs, all structural:

1. **The anchor must be reproduced byte-exact** — so every edit is preceded by a search, and the
   agent pays to carry the anchor text in context.
2. **Uniqueness fails exactly where the document is most repetitive** — table rows, `Verify:` lines,
   `| ACCEPTED |` cells. Precisely the state you most want to update.
3. **No revision token.** Two agents editing the same file both succeed and the second silently wins.
4. **No semantics.** "Close assumption A6" is not an operation; it is a string substitution that
   happens to produce a `~~`.

### The design conclusion

> **Give blocks stable ids and the anchor problem disappears.**

If a document is stored as an ordered list of blocks, each with an id, then a read renders
`[#b7] ## Standing constraints …` and a patch is `{block: "b7", op: "set", body: "…"}`. That is
**strictly cheaper and strictly safer than `Edit`**: no anchor to reproduce, no uniqueness collision,
no prior search, and a per-block `revision` gives real lost-update detection.

And the deeper move: **most writes should not be document patches at all.** "Task 2.3 accepted,
commit `1b6034b5`, `dotnet test --filter Projector` 187/187" is not a paragraph — it is a row. The
only genuinely narrative fields are the goal, the brief body, and a handful of notes. Everything
else in those 55,523 lines is a row that was serialised into prose because prose was the only store
available.

---

## 5. The three tiers map onto one tree

`controller` → `orchestrate` → `Agent` is already a hierarchy, and the corpus shows it physically:
`Gaia/prod-log-triage/` holds `FINDINGS.md` + `ACTIONS.md` at the queue level and
`fixes/B-09/LEDGER.md` per unit. The user's `projects > features > plans > epics > tasks > sub-tasks`
is the same shape generalised.

So: **one recursive `item` table**, `kind` as configurable data, depth unlimited. A "run" is an item.
A "phase" is an item. A "finding" is an item. A "deferred follow-up" is an item. That is what makes
*"show me everything still open under this project"* a single query instead of 25 greps.

**But hierarchy is not dependency.** Task 3.1 depends on 2.4's endpoint while living in a different
phase; `brief-1.3r` is a *rework of* 1.3; `4.6b` *supersedes* part of 4.6. Containment is a tree;
`blocks` / `supersedes` / `duplicates` / `derived_from` is a DAG over it. Two separate structures,
both first-class. The user's "unclosed dependencies" query is the DAG one.

---

## 6. Why the model will actually maintain this (the forced-closure problem)

The user's hardest requirement: *"the model should be forced to close things that are done."* Files
cannot do this because nothing rejects a lie. Six mechanisms, in descending strength:

1. **Refuse the transition.** `work_status(item, done)` fails with `isError: true` unless every
   done-criterion is checked, a `verification` row exists with `passed = true` and `run_by =
   orchestrator`, and no child item is open. The error names the missing pieces *and the exact tool
   calls that supply them*. An agent that cannot close a task has to either do the work or make an
   explicit, recorded decision to defer it.
2. **Deferral is a move, not a status.** You cannot mark something "deferred" and walk away; you
   `work_status(defer, target: <item or backlog>, reason)`, which **creates a real open item
   elsewhere**. Nothing evaporates, which is precisely how the 151 "Deferred" mentions in the corpus
   stop being a graveyard.
3. **The attention channel on every result.** Every tool result carries an `attention` block, capped
   at ~5 lines: stale `in_progress` claims, assumptions past their `revisit_when`, unconsumed notes
   addressed to the current scope, items closed without verification. Agents read tool results
   reliably — this costs nothing and nags continuously, which is exactly what the 1,241 ⚠ markers
   were trying to do by hand.
4. **Scoped tokens make roles structural.** `tools/list` "**MAY** vary by the authorization presented
   on the request" (2026‑07‑28 spec). A worker's token sees `work_get`, `work_report`, `work_note`
   and nothing else — so *"workers never commit, the orchestrator judges"* becomes a property of the
   protocol rather than a sentence in a brief that gets forgotten.
5. **Claim leases.** `in_progress` requires a claim with a TTL and a heartbeat. An expired claim
   auto-releases with an event and surfaces in `attention` — which is the machine answer to
   `nested-claude`'s "a parked agent is indistinguishable from a busy one".
6. **Evidence is required, not narrated.** `verify_cmd` is `NOT NULL` on a task. A verification row
   stores the **actual result line**, not an exit code — the skill's own rule (*"an `exit 0` at the
   end of a pipe belongs to the `tail`"*), enforced by a required field.

---

## 7. What the store must **not** try to own

- **The spec.** `SPEC.md` is a team artifact that belongs in git next to the code it describes, and
  the skills already say `PLAN.md` must be *"a thin task list that points at it"*. Keep it in the
  repo; reference it by path + commit sha.
- **Diffs and code.** Reports carry `FILES:` one line each and *"no code blocks"*. Store commit shas
  and paths; git holds the bytes.
- **Transcripts.** `~/.claude/projects/<slug>/<sessionId>.jsonl` already exists and is the documented
  recovery path. Store the pointer.
- **Anything git already versions.** Duplicating it creates the exact "two overlapping planning
  artifacts, you maintain both and trust neither" failure the skills warn about.

---

## 8. What this buys, in the corpus's own terms

| Today | With the store |
|---|---|
| A fresh orchestrator reads a 120-line header block and hopes it is current | `work_brief(scope)` — a generated projection that **cannot** be stale |
| Plan audit spends 60–150k to check numbering and dependencies | constraints; the audit spends its budget on *shape*, which is the finding that pays |
| "Carry into later briefs" delivered by memory | notes **addressed** to an item, auto-injected when its brief renders, marked consumed |
| 96 supersessions, 129 corrections | `status`, `supersedes` edges, and block revisions |
| 17/25 runs with stale checkboxes | there is no second copy of the truth to go stale |
| `grep -c '⚠'` | `work_threads(scope)` |
| The ledger disagrees with the code | the ledger *is* the state the transitions produced |
