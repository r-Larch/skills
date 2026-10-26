# 06 — The renderer: exact output formats

> The probe settled it: **the text block is the entire product** (doc 01 §2). There is no JSON half
> to fall back on, so every tool result is judged on one question — *can a model act on this without
> a second call?*
>
> `WorkState.Core` owns these renderers. They are **domain logic, not presentation**, and they are
> unit-tested for shape and for token cost.

---

## 1. Five rules, in priority order

1. **The first token of a line is the handle.** `ref` leads every row, always. An agent reading a
   board must be able to act on any line without scanning it.
2. **Ids never appear.** `ref` is the only address. There is no second channel to hide a uuid in, and
   a uuid the agent cannot use is 12 wasted tokens per row.
3. **Columns are fixed-width and left-aligned.** Measured: real line breaks and column alignment
   survive to the model intact. Alignment is not decoration — it is what makes a 40-line board
   scannable instead of read.
4. **Absent means normal.** Only exceptions get a marker. A board where every row carries `ok` has
   spent its budget saying nothing.
5. **Every result ends with the attention block, or nothing.** Never an empty header.

**Width budget: 100 columns.** A long text wraps at 100 columns under an indented continuation; it
is shortened only where the section below allows it. Refs, status and evidence never are.

### What may be shortened

Text is shortened with `…` in exactly two places, and only by `Glyph.Row`:

1. **A one-line row of a list view** — `work_tree`, `work_next`, `work_search`, board rows, the
   finding and followup lines of `work_threads`, attention lines, the head row of `work_get` — and
   only when the row carries the ref of the thing, so `work_get(ref)` prints it whole.
2. **A write confirmation echoing the caller's own input** — `OK  note recorded` and the preview of
   the body just sent.

Everywhere else text is printed **whole**: every `work_brief` section (GOAL, BINDING, assumptions,
notes, LAST VERIFY, the worker task's title, criteria and `verify_cmd`), every detail view
(`work_get`, `work://item/<ref>`, `work://item/<ref>/brief`), the bodies `work_threads` and
`work_history` are the only full copy of (assumption statements, addressed notes, event summaries),
every refusal, route and `Next:` trailer, and anything a reader copies or runs — commands, refs, tool
calls — even inside a list row (a row drops a route rather than print a cut one). What the server
stores is stored whole too: a title, a result line or an event summary is never cut on the way in.

A **count** may be capped (`+2 more`) only when the same line names the call that lists all of them.
A text with no detail view at all is a gap, closed by printing it whole where it is listed — which is
why `work_get` prints every `work_report` an item received, and `work_threads` an assumption's
statement and an addressed note's body.

The exceptions are an allow-list in `WorkState.Tests/Read/NoClipSourceTests.cs`: a call to
`Glyph.Row` from any other method, `Clip(`, or a hand-made cut (`[..n]`, `Substring`, `left(`, a glued
`"…"`) fails the build, and adding an entry is a visible review decision. `NoClipBehaviourTests`
fills every text field with a 300-character sentinel and asserts each view shows it whole.

> **Changed 2026-10-26 (noclip).** The fifth instance of the same bug — a `verify_cmd` cut to 44
> characters *inside* the printed `work_verify(…)` route of the done-guard refusal, a route that could
> not be copied and run — after intent and criteria in `work_get`, evidence lines, and notes in the
> brief. `Glyph.Clip` had ~80 call sites in 15 files; it is gone.

---

## 2. The item row

```
<ref>  <kind>  <status>  <title>                        <evidence>  <flags>
```

| Field | Width | Notes |
|---|---|---|
| `ref` | 6 | left, truncation forbidden — it is the handle |
| `kind` | 6 | `task`, `phase`, `find`, `foll`, `assum`, `run`, `proj` |
| `status` | 9 | `open`, `ready`, `doing 43m`, `blocked`, `review`, `done`, `cancel`, `super` |
| `title` | 34 | truncated with `…` — a row; `work_get` prints it whole |
| `evidence` | 12 | `✓12/12`, `✗2/14`, `—` |
| `flags` | rest | only when abnormal |

```
2.3   task    done      EntityProjector                     ✓187/187  a1b2c3d
2.4   task    doing 43m Run service + Quartz                —         ⚠ no verify
2.5   task    blocked   Admin API                           —         ← 2.4
2.6   task    open      Economics gate test                 —         review-req
F-07  find    open      ContentHash written before the work —         BLOCKER → 2.4
A6    assum   open      Docker can run pgvector             probed    revisit: 1.1
```

**Flag vocabulary** — each is one token and each names a *state that needs a decision*:

| Flag | Means | The call that resolves it |
|---|---|---|
| `⚠ no verify` | `done`-eligible but no orchestrator verification (never on a deferred, `obsolete` item) | `work_verify` |
| `← 2.4` | blocked by `2.4` | finish `2.4` |
| `review-req` | `risk_tags` non-empty | spend a review before closing |
| `BLOCKER → 2.4` | a blocker finding targeting `2.4` | close or defer the finding |
| `revisit: 1.1` | assumption due when `1.1` closes | `work_assume resolve` |
| `claim exp 13m` | lease expired | release or heartbeat |
| `3 open` | children still open | close them, or the parent cannot close |

---

## 3. `work_tree` — indentation carries the hierarchy

Two spaces per level. Rollups on containers only.

```
gaia                          proj   —          Gaia
  entity-store                run    doing      Entity store          12/24 done
    1 Foundations             phase  done       ✓                      5/5
    2 Ingestion               phase  doing      gate not run           6/8
      2.7 jsonb polymorphic   task   done       ✓181/181  a4ec6bb
      2.8 ContentHash fix     task   doing 43m  ⚠ no verify
      F-07 ContentHash early  find   open       BLOCKER → 2.8
    3 Query surface           phase  open       ← 2                    0/6
```

A `phase` line shows `done/total` for its subtree. `—` where a rollup is meaningless.

---

## 4. `work_brief` — the recovery payload

The one call that starts every session. Replaces a `LEDGER.md` header block, **generated so it cannot
go stale.** It is paid on every session start, so the number of rules, assumptions and notes is
capped, each cap naming the call that lists the rest — but every text it does show is whole ("What
may be shortened", §1). Target **≤ 65 lines and ≤ 1,250 tokens** on the committed sample-run fixture.

> **Changed 2026-10-26 (noclip).** The target was ≤ 35 lines / ≤ 900 tokens, met by cutting GOAL to
> three lines and each rule and note to one. Printed whole, the fixture renders 56 lines and 1,063
> tokens (it was 35 and 607); the bound is that plus ~15%.

```
RUN entity-store  ·  project gaia  ·  branch feat/entity-store @ 7cd34161
GOAL The voice agent and DESK answer from a curated entity store instead of page-chunked
     PDFs, provably without re-embedding unchanged records.

WHERE  phase 2 of 9 — Ingestion. 6 of 8 tasks done, gate not yet run.
NEXT   work_status(ref:"2.8", to:"in_progress")   ← the literal next action

BINDING (4)
  ! C1  never modify Gaia.Web/Core/Rag/** — 23 live chatbots depend on it
  ! C2  production is read-only until phase 9
  ! C4  run the suite with --filter; unfiltered hits a live service
  · C9  regenerate: dotnet ef migrations add, never edit the snapshot by hand

OPEN ASSUMPTIONS (2)
  A3 DSS jsonexport reachable from a dev machine   untested   medium   revisit: 3.1
  A4 EAV beats jsonb+GIN for enumeration           reasoned   medium   revisit: phase-4 gate

ADDRESSED TO YOU (2)   — these render into 2.8's brief and are then marked consumed
  facets must be replaced per entity per run (delete-then-insert), assert ROW COUNTS
  truncate facet values to 512 before insert — varchar(512) throws 22001 and aborts

LAST VERIFY  dotnet test --filter Importer → 181/181, by orchestrator, 6m ago

--- attention (2) ---
⚠ A3 is past revisit_when (3.1 closed 20m ago) — work_assume(ref:"A3", resolve:…)
⚠ 2.8 claim expired 13m ago — work_status(ref:"2.8", to:"in_progress") to retake it
```

**`NEXT` is a literal, callable line.** An agent that only ever does what `NEXT` says is running the
system correctly — that is the tutorial channel of doc 04 §4, and it is why this line is never prose.

**A closed scope's `NEXT` never closes it again.** When no child is open, an open scope's `NEXT` is
`work_status(ref:"<ref>", to:"done") — every child is closed`. A `done`, `cancelled` or `superseded`
scope instead names the first open item derived from it or from anything beneath it (a
`derived_from` link, which is what a deferral writes), wherever that item now sits:
`work_get(refs:["<ref>"]) — m1 is done; <ref> derived from it is still open`. With no such item the
line is `nothing left — m1 is done`.

> **Changed 2026-10-23 (donenext).** `work_brief(scope:"autowork/m1")` said
> `NEXT work_status(ref:"m1", to:"done")` while m1 was done, and that call was refused "m1 is done".
> The last branch checked only the children and never the scope's own status.

**A BINDING line carries its ref**, between the marker and the rule, in the same four-wide handle
column the assumption rows below it use. The marker still opens the line, so the `!` column stays
scannable at a glance; the ref is what makes the rule addressable, which is what `work_constrain`
needs to retire one and what a finding needs to argue against one.

> **Changed 2026-09-22 (U9.1).** This section previously specified `  ! <text>`, with no ref —
> deliberately, on the grounds that a rule asks for no decision. It was wrong: `constraint_rule` had
> no `ref` column at all, so the omission was not restraint, it was the schema showing through. A
> rule an agent can read but cannot name is one it cannot cite, question or retire, and directly
> below it the assumptions print as `A3`. This is the only change to a rendered surface in the U9
> queue, and it is why the unit that made it also edited this file.

**In the worker brief, rules and intent are never cut.** `work_brief(for:"worker")` and
`work://item/<ref>/brief` render each BINDING rule with its route, the GOAL and the task's `intent`
in full, wrapped at 100 columns under an indented continuation:

```
  ! C1   Never stop, rm, respawn, attach to, message, or read the key file of any Claude session
         this controller did not start itself — report it to the controller and leave it as it is
```

The resume brief (`for` omitted) wraps them the same way since noclip; it differs only in showing at
most five rules, with `+n more — read work://workspace/conventions` for the rest.

> **Changed 2026-10-22 (ws/2).** Both briefs previously clipped a rule to one ~95-column line, GOAL to
> three, and the worker brief's `intent` to one. A controller pastes the worker brief into a worker's
> prompt, so the worker received red lines with their ends missing — and the end is the route, the
> "what to do instead". Wrapping rules in the resume brief too was measured and rejected: on the
> committed sample-run fixture it renders 41 lines against the 35-line target. The worker brief also
> lists every rule in scope, with no count cap and no `+n more` pointer: a pasted brief has no
> pointer a worker can follow, so the resume brief's cap of five silently dropped rule six and up.

**The worker brief is worker-shaped.** A worker does not own its task's status — the orchestrator
claims, verifies and closes, and a worker token cannot call `work_status` — so the worker variant's
`NEXT` names the hand-back, never a transition, and its done-criteria and the notes addressed to the
task wrap in full like the rules do:

```
NEXT   do the task, then work_report(ref:"2.8", status:"done", files:[…], verify:"…", …)
…
YOUR TASK  2.8 — ContentHash fix
  criteria
    ✗ c3 numbers recorded in docs/LEARNINGS.md: build time, binary size, idle RSS, p95 first-byte
         latency under the battle-test load
  report    status / files / verify / red_check / deviations / assumptions / risks / not_done
  brief     work://item/2.8/brief
  Next: work_report(ref:"2.8", …) with the fields above; closing is the orchestrator's call.
        work_verify(ref:"2.8", …, run_by:"worker") records a claim, not evidence.
```

The resume brief's `NEXT` is unchanged; its notes wrap whole as well (noclip). When the notes past its
cap of three name no item, the overflow line routes to `work_history()`, which prints each note's
event whole — `work_threads` lists only addressed notes.

> **Changed 2026-10-22 (ws/3).** The worker brief's `NEXT` was the resume brief's — on a claimed task
> `work_verify(…, run_by:"orchestrator") then work_status(ref, to:"done")`, on an open one
> `work_status(ref, to:"in_progress")` — and its trailing line named only `work_verify`. Both told a
> worker to make a call it does not own. Its criteria were clipped at 80 columns and its notes at 94,
> so a worker read `p95 f…` for the criterion it is judged against.

> **Changed 2026-10-22 (ws/4).** The worker brief rendered three addressed notes and then consumed
> every note addressed to the task, so the fourth and later were marked delivered unseen. It now
> renders every note addressed to its own item (notes it merely carries keep the cap of three), and
> consumption marks exactly the rendered notes by id — a note written between render and consume
> stays for the next brief.

**`work_get` (and `work://item/<ref>`, which renders the same view) prints in full.** Intent, each
criterion and each evidence line wrap at 100 columns under an indented continuation, like the worker
brief. The boards — `work_tree`, `work_next`, `work_search` — keep one clipped row per item; `work_get`
is the call such a row routes to. Its own head row is such a row, so a title longer than the column
is printed again whole on a `title` line beneath it; every evidence line is listed (it showed the
latest two), and every `work_report` the item received follows as a `report` line.

> **Changed 2026-10-26 (tool-text).** `work_get` is described as "full detail" but clipped `intent` to
> 86 columns, each criterion to 80 and each evidence result line to 44.

**The note header names what it is holding.** `ADDRESSED TO YOU (n)` counts only notes that name a
target item — those are the ones a worker brief renders and consumes. Where a run carries unconsumed
notes that name nobody, the section is instead:

```
NOTES IN SCOPE (140)   — carried forward by this run; none names an item, so none is consumed
```

> **Corrected 2026-09-20 (FINDINGS-live F-L3).** The real run rendered `ADDRESSED TO YOU (140)` over
> 140 notes, not one of which named a target: an honest count under a dishonest label, and the label
> is the part an agent reads.

**On an empty server** `work_brief` returns onboarding instead: what a project is, what makes a good
`verify_cmd`, and the one call to make next. Cold start uses the same mechanism as every other path.

### 4a. The project brief

`work_brief` on a project — `scope:"<project>"`, or `repo:` / a minted session's bare call, which
resolve to the bound project (doc 02) — is the continue-here context of the repository. Nobody works
*on* a project, so its `NEXT` never transitions it: it resumes the busiest open run, or starts one.

```
PROJECT e2e  ·  End to end  ·  repo remote github.com/r-larch/e2e, root p:/projects/e2e
GOAL <the project's intent, whole>

OPEN RUNS (2)                      every open run but the backlog run
  run-a — The run
    0/2 tasks done · held: 1.2 by aw-alpha
    next  work_get(refs:["1.1"]) then work_status(ref:"1.1", to:"in_progress")
  run-b — The other run
    0/1 tasks done
    next  nothing ready — 9.1 is blocked; work_threads(scope:"run-b", include:"deps")

RECENT (5 of 7 closed)             newest first: outcome, closed date, last commit in the run
  m7 — Closed run 7 — done/accepted · 2026-10-23 · c0ffee07
  · +2 older — work_tree(scope:"e2e", depth:1, status:"done,cancelled,superseded")

BACKLOG (6 open)                   open leaves of the backlog run, then follow-ups elsewhere
  b1     foll   Backlog follow-up 1
  · +1 more — work_tree(scope:"backlog") and work_threads(scope:"e2e", include:"followups")

BINDING / NOTES IN SCOPE / LAST VERIFY      as the run brief prints them

OPEN ASSUMPTIONS (7)               every open one under the project, qualified: runs number their own A1
  run-a/A1 Assumption number 1 still holds.   reasoned   medium
  · +2 more — work_threads(scope:"e2e", include:"assumptions")

NEXT   resume run run-a — work_brief(scope:"run-a")
```

An assumption's ref reads back in `work_get(refs:["run-a/A1"])`, which prints it whole: statement,
state, evidence, reversal, revisit, scope, the resolution once settled, and its import source and why
when it was imported. A bare `A1` held by two runs is refused with the qualified forms, as an item
ref is.

> **Changed 2026-10-26 (assumption-reach).** `work_get(refs:["A1"])` answered "no item has ref A1",
> and the project brief showed none of the project's open assumptions.

With no open run, `NEXT` is `start a run under this project — work_create(items:[{kind:"run",
parent:"e2e", ref:"<slug>", title:"…"}])`. Titles and notes print whole; recent runs are capped at
five and backlog rows at five, each cap naming the call that lists the rest. `next` is never wrapped,
so the call copies whole. Budget: 49 lines / 696 tokens on the `ProjectBriefTests` fixture, bounded
at 56 / 800; the run brief keeps its own 65 / 1250.

> **Changed 2026-10-26 (project-brief).** A project scope rendered the run brief's shape — `WHERE 3 of
> 3 tasks done.` and `NEXT work_status(ref:"e2e", to:"in_progress")` — with no runs, no history and
> no backlog.

---

## 5. The attention block

Appended to **every** result, read or write. **Capped at 5 lines**, ranked by cost-of-ignoring.
Omitted entirely — header included — when empty.

```
--- attention (2) ---
⚠ A3 is past revisit_when (3.1 closed 20m ago) — work_assume(ref:"A3", resolve:…)
⚠ 2.8 claim expired 13m ago — work_status(ref:"2.8", to:"in_progress") to retake it
```

Every line is `<what> — <the call that fixes it>`. A line without a call is a defect: it is the
1,241 ⚠ markers of the old corpus reinvented, and those are what this replaces.

When more than 5 lines are due, the header counts both and a sixth line names the read that lists
them all, as "What may be shortened" requires of a count cap:

```
--- attention (5 of 9) ---
⚠ …five ranked lines…
+4 more — read work://attention/run-a with ReadMcpResourceTool
```

`work://attention/<scope>` prints every line, ranked, uncapped (doc 01 §8). No tool lists exactly
these lines — expired claims and phase gates appear nowhere else — so the route is that resource.

> **Changed 2026-10-26 (attention-cap).** The header gave the shown count and nothing listed the
> rest: the one count cap that broke the noclip rule.

Ranking: expired claims → blocker findings → **phases whose tasks are all done and whose gate has not
run** → items done-but-unverified → assumptions past revisit → unconsumed notes for the current scope
→ stale `in_progress`.

```
⚠ phase-2: all 8 tasks done, gate not run — work_brief(scope:"phase-2") to run it
```

> **Why that line exists, 2026-09-20 (FINDINGS-live F-L1b).** Nothing promotes a phase when its last
> child closes, on purpose: a write triggered by another write is invisible and untestable, and a
> phase whose tasks are done is *ready to be gated*, not done. It outranks done-but-unverified
> because what goes unexamined is the composition across every task in the phase, not one task's
> missing evidence.

**A moved item is not done-but-unverified.** A deferral closes its item `done` with resolution
`obsolete`, because the work moved rather than finished (doc 07 §4). That item is left out of the
attention line, `work_audit`'s `done-unverified` check, the `⚠ no verify` row flag and the honesty
strip's count, so all four numbers agree. `wontfix` and `duplicate` closures stay in: they pass the
same done guard as `accepted`, so they cannot close without an orchestrator verification unless
`force:true` is used, and a forced close is the case the count exists to show.

> **Changed 2026-10-23 (defer-verify).** A deferred item raised `<ref> is done with no orchestrator
> verification — work_verify(…)`, which invites a verification of work that was never done here.

**The unconsumed-note line is one line per addressee**: `N note(s) addressed to <ref> unconsumed —
work_brief(scope:"<ref>", for:"worker")`, where `<ref>` is bare unless another item in the workspace
shares it, in which case it is qualified (`run-one/4.1`), the same rule as ws/10's calls and URIs.

> **Changed 2026-10-22 (ws/13).** The line grouped on the bare ref, so at project scope the notes to
> `run-one/4.1` and `run-two/4.1` were one line whose `work_brief(scope:"4.1")` is refused as
> ambiguous: a hint that could not fire, over two briefs that each consume only their own notes.

**A closed addressee raises no line.** Only the addressee's worker brief consumes a note, and nobody
briefs a `done`, `cancelled` or `superseded` item, so its notes leave attention. They stay unconsumed
and readable in `work_threads(include:"notes")` and the history. When the item is superseded through
`work_link(type:"supersedes")`, the link moves its unconsumed notes to the replacing item in the same
transaction, because they are still somebody's obligation. The line then names the replacement.

> **Changed 2026-10-23 (F-08).** A note addressed to a closed item repeated on every call:
> `1 note(s) addressed to 8-d unconsumed` long after 8-d was superseded, and the same for `m2/1` after
> it was done. The line's call was a brief nobody would run.

---

## 6. Refusals

A refusal is the highest-value text the server emits — it is read at the exact moment it is relevant,
and it teaches the contract permanently in one round trip (doc 04 §3).

```
REFUSED  work_status(2.4 → done). 3 conditions unmet.

  ✗ criterion c2 "DeletesSkipped surfaced on DatasetSourceRun" unchecked
      → work_update(ref:"2.4", criteria:[{id:"c2", checked:true, evidence:"…"}])
  ✗ no verification with run_by=orchestrator (1 exists, reported by the worker)
      → work_verify(ref:"2.4", command:"dotnet test --filter Runs",
                    result_line:"Passed! 187/187", run_by:"orchestrator")
  ✗ finding F-07 (BLOCKER) targets 2.4 and is open
      → work_status(ref:"F-07", to:"done")   or
        work_status(ref:"F-07", to:"deferred", target:"backlog", reason:"…")

  Override: work_status(ref:"2.4", to:"done", force:true, reason:"…") — recorded as a
  forced_transition, which work_audit lists permanently.
```

**Rules, all load-bearing:**

- **Every `✗` is followed by a `→` that is a callable line.** No exceptions. An error that states a
  problem without naming the fix is a defect against doc 04 §3.
- **List every unmet condition**, never just the first. Three round trips to discover three problems
  is three times the cost and reads as an adversarial system.
- **Name the override, and name its cost.** An escape hatch that is hidden gets worked around; one
  that leaves a permanent mark gets used honestly. Never hide `force`.
- **A route is a call that binds.** Every named argument in a `→` line or a `NEXT` line is one the
  tool declares, in the shape it declares (doc 01 §3.6). A call the server would refuse, printed as
  the fix, teaches the wrong call permanently.

A call whose arguments cannot bind is refused before the tool runs, in the same shape, with no
override because nothing ran:

```
REFUSED  work_get — 1 argument cannot bind. Nothing ran.

  ✗ refs is required and was not sent — One or more refs, e.g. ["2.3", "F-07"]. Qualify across runs: "entity-store/2.3".
      → work_get(refs:["…"])
```

---

## 7. What gets tested

Renderer tests are cheap and they are the only defence against drift back toward prose.

- [ ] Every row's first token is the `ref`.
- [ ] **No uuid appears in any rendering, ever** — one regex over the output of every renderer.
- [ ] Column positions hold for refs up to 6 chars and titles up to 34, and long titles get `…`.
- [ ] A clean result emits **no** attention header, not an empty one.
- [ ] The attention block never exceeds 5 lines, and **every line contains a `—` followed by a call**;
      past 5 a final `+N more — read work://attention/<scope>` line names the read that lists all.
- [ ] A refusal lists **all** unmet conditions and every one has a `→` line.
- [ ] **Token budget, asserted with a number — against a committed fixture, not the live corpus.**
      `work_brief` on a fixture run imported into the test container renders in **≤ 1,250 tokens**
      (≤ 900 until noclip printed every shown text whole, §4).
- [ ] **No text is cut outside a list row or an echo** — `NoClipSourceTests` (source scan against the
      allow-list) and `NoClipBehaviourTests` (300-character sentinels in every field, every view).

      > **Corrected 2026-09-20.** The earlier version of this line said "the imported `entity-store`
      > run", which is **not deterministic**: those corpora are other projects' live working records
      > and one of them grew from 181 to 182 files *during this build*. A build-failing test must
      > measure something committed and frozen, or it fails for reasons that have nothing to do with
      > the renderer.
      >
      > The **headline claim** — `work_brief` against the real 6,011-line `entity-store` ledger — is a
      > **one-off measurement recorded in the ledger with the date and the corpus state**, not a
      > gate. Both numbers are worth having; only one of them belongs in CI.
