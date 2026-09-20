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

**Width budget: 100 columns.** Long titles truncate with `…`; refs, status and evidence never do.

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
| `title` | 34 | truncated with `…` |
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
| `⚠ no verify` | `done`-eligible but no orchestrator verification | `work_verify` |
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
go stale.** Target **≤ 35 lines**; it is paid on every session start.

```
RUN entity-store  ·  project gaia  ·  branch feat/entity-store @ 7cd34161
GOAL The voice agent and DESK answer from a curated entity store instead of page-chunked
     PDFs, provably without re-embedding unchanged records.

WHERE  phase 2 of 9 — Ingestion. 6 of 8 tasks done, gate not yet run.
NEXT   work_status(ref:"2.8", to:"in_progress")   ← the literal next action

BINDING (4)
  ! never modify Gaia.Web/Core/Rag/** — 23 live chatbots depend on it
  ! production is read-only until phase 9
  ! run the suite with --filter; unfiltered hits a live service
  · regenerate: dotnet ef migrations add, never edit the snapshot by hand

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

**On an empty server** `work_brief` returns onboarding instead: what a project is, what makes a good
`verify_cmd`, and the one call to make next. Cold start uses the same mechanism as every other path.

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

Ranking: expired claims → blocker findings → items done-but-unverified → assumptions past revisit →
unconsumed notes for the current scope → stale `in_progress`.

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
  forced_transition, permanently visible in work_brief and work_audit.
```

**Rules, all load-bearing:**

- **Every `✗` is followed by a `→` that is a callable line.** No exceptions. An error that states a
  problem without naming the fix is a defect against doc 04 §3.
- **List every unmet condition**, never just the first. Three round trips to discover three problems
  is three times the cost and reads as an adversarial system.
- **Name the override, and name its cost.** An escape hatch that is hidden gets worked around; one
  that leaves a permanent mark gets used honestly. Never hide `force`.

---

## 7. What gets tested

Renderer tests are cheap and they are the only defence against drift back toward prose.

- [ ] Every row's first token is the `ref`.
- [ ] **No uuid appears in any rendering, ever** — one regex over the output of every renderer.
- [ ] Column positions hold for refs up to 6 chars and titles up to 34, and long titles get `…`.
- [ ] A clean result emits **no** attention header, not an empty one.
- [ ] The attention block never exceeds 5 lines, and **every line contains a `—` followed by a call**.
- [ ] A refusal lists **all** unmet conditions and every one has a `→` line.
- [ ] **Token budget, asserted with a number — against a committed fixture, not the live corpus.**
      `work_brief` on a fixture run imported into the test container renders in **≤ 900 tokens**.

      > **Corrected 2026-09-20.** The earlier version of this line said "the imported `entity-store`
      > run", which is **not deterministic**: those corpora are other projects' live working records
      > and one of them grew from 181 to 182 files *during this build*. A build-failing test must
      > measure something committed and frozen, or it fails for reasons that have nothing to do with
      > the renderer.
      >
      > The **headline claim** — `work_brief` against the real 6,011-line `entity-store` ledger — is a
      > **one-off measurement recorded in the ledger with the date and the corpus state**, not a
      > gate. Both numbers are worth having; only one of them belongs in CI.
