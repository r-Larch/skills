# 15 — Query plans against a 5,000-item workspace

Owner: aw-m33 (backlog/explain-5k). Measured 2026-10-27 on postgres:17 (Testcontainers), branch
`ws/explain-5k` off `feat/workstate` @ fc45f08.

Doc 03 task 1.4 promised *"hand-written SQL, EXPLAIN-checked against a seeded 5,000-item workspace"*.
That check had never been run. This doc is the result. Two reads were over budget and several
hot reads seq-scanned `item`; §4 lists what was fixed, and §3 has the before/after numbers.

## 1. How it is measured

`mcp/WorkState.Tests/Store/QueryPlanTests.cs` is **opt-in**. It is gated on `WORKSTATE_EXPLAIN=1`
through xunit v3's `[Fact(Skip = …, SkipUnless = nameof(Enabled))]`, so a default `dotnet test`
reports it as skipped and does no work:

```
dotnet test mcp/WorkState.slnx --filter FullyQualifiedName~QueryPlanTests -e WORKSTATE_EXPLAIN=1
```

The test creates a throwaway database in the suite's own `PostgresFixture` container, applies every
migration, seeds it (§2), and runs `VACUUM ANALYZE`. It then turns on **`auto_explain`** for that
database (`log_analyze`, `log_buffers`, `log_timing`, json format) and calls the **real query
methods**: `WorkStateQueries`, `BoardQueries`, `ApiQueries`. The plans are therefore the ones for
the statement the server actually sends, with the values it actually binds. Each plan is read back
from the server log, where a `RAISE LOG` marker before each call labels it. Every query runs 5
times, and the median run is reported. The full plans go to `explain-5k.txt` beside the test
assembly. Runtime is about 12 s for the test itself (seed about 3 s).

**Budget:** a hot read over this seed that takes more than **50 ms**, or that seq-scans a large
table with no index path to fall back on, needs an index or a rewrite. Anything else is left alone.

## 2. The seed

Bulk SQL (`PlanSeed.cs`), not the writers. A writer call costs a transaction plus a journal round,
and only the read side is being measured. The `ancestors`/`ref_scope_id` triggers still run on every
insert, and items go in parent before child. The seed is deterministic (`Random(5000)`).

| table | rows | | item kind @ depth | rows |
|---|---:|---|---|---:|
| item | **4,955** | | project @0 | 8 |
| event (journal) | **20,000** | | run @1 | 20 |
| item_criterion | 5,321 | | followup @1 (project backlog) | 240 |
| verification | 3,223 | | phase @2 | 103 |
| claim | 1,204 | | task @3 | 856 |
| item_link (blocks + derived_from) | 1,041 | | subtask @4 | 2,753 |
| note | 900 | | finding @4 / @5 | 166 / 407 |
| artifact (commits) | 441 | | followup @4 / @5 | 119 / 283 |
| assumption | 156 | | | |
| constraint_rule | 60 | | | |
| agent_session | 40 | | | |

The seed has 8 projects and 20 runs. Six runs are finished, and one project is archived. The
**busiest run**, `autowork/m1`, holds 698 items; its project holds 1,367. Statuses follow each run's
progress: phases behind it are done, the phase at it is mixed, and phases ahead are open. A fifth of
the finished phases are left ungated. Some in_progress items hold live claims, some hold lapsed ones,
and some are stale. A few done items have no orchestrator verification. 30% of notes are unconsumed.
Blocks edges point forward in document order (never a cycle), and every follow-up is `derived_from`
its parent.

Each query is measured at the busiest **run**, at its **project** and, where the query takes no
scope, at the whole **workspace**.

## 3. Results

Times are the median of 5 runs (`duration` from auto_explain, i.e. execution including JIT).
*Before* is fc45f08. *After* is this branch: `RefLookupIndex`, the lateral rollup, and `jit=off`.

| query | scope | before ms | of which JIT | after ms | verdict |
|---|---|---:|---:|---:|---|
| ReadyFrontierAsync | run / project | 7.0 / 9.9 | 0 | 6.0 / 9.8 | **fine** |
| RollupAsync | run / project | 0.32 / 0.55 | 0 | 0.31 / 0.69 | **fine** |
| DefaultScopeAsync | workspace / project | 20.6 / 5.0 | 0 | 3.7 / 1.1 | **rewrite** (full-view rollup) |
| ResolveAsync bare `3.4` | workspace | 1.8 | 0 | 0.14 | **index** |
| ResolveAsync `m1/3.4` | workspace | 1.7 | 0 | 0.16 | **index** |
| ResolveAsync `autowork/m1/3.4` | workspace | 14.1 | 13.0 | 0.14 | **index** |
| ResolveAsync `autowork/B-3` (fallback) | workspace | 1.6 | 0 | 0.07 | **index** |
| SubtreeAsync (work_tree, every work_brief) | run / project | 26.4 / 44.7 | 0 / 14.6 | 7.6 / 15.3 | **rewrite** + jit |
| ApiQueries.BoardAsync (`/api/v1/board`) | run / project | **67.3 / 77.1** | 34.4 / 34.8 | 16.6 / 31.5 | **rewrite** + jit |
| OpenThreadsAsync (work_threads) | run / workspace | 3.2 / 3.4 | 0 | 3.4 / 3.4 | **fine** |
| SearchAsync | 2 / 3 words | 0.74 / 0.50 | 0 | 0.55 / 0.37 | **fine** (items only; see §3.1 for the union) |
| attention: ExpiredClaims | run / project | 0.60 / 1.06 | 0 | 0.57 / 0.84 | **fine** |
| attention: BlockerFindings | run / project | 0.14 / 0.28 | 0 | 0.11 / 0.23 | **fine** |
| attention: UngatedPhases | run / project | 0.58 / 1.00 | 0 | 0.60 / 0.90 | **fine** |
| attention: DoneUnverified | run / project | 0.81 / 1.30 | 0 | 0.58 / 1.13 | **fine** |
| attention: Assumptions (open) | run / project | 0.39 / 0.68 | 0 | 0.31 / 0.51 | **fine** |
| attention: UnconsumedNotes (open addressees) | run / project | 3.2 / 3.2 | 0 | 2.7 / 3.1 | **fine** |
| attention: FactsAsync (addressees) | run / project | **54.6 / 66.7** | 0 | 0.62 / 1.16 | **index** + **rewrite** |
| attention: StaleInProgress | run / project | 0.22 / 0.36 | 0 | 0.28 / 0.39 | **fine** |
| journal `GET /api/v1/journal` head | workspace | 0.07 | 0 | 0.06 | **fine** |
| journal `?before=` | workspace | 0.06 | 0 | 0.06 | **fine** |
| journal `?before=&scope=` (run) | run | 1.43 | 0 | 1.53 | **fine** |
| journal `?before=` deep (seq/10) + run | run | 0.29 | 0 | 0.32 | **fine** |

Buffers are all shared hits (`read=0`): the seed fits in shared_buffers, so these are warm-cache
numbers. The rows above were measured with search over the item tsvector only (`item_search_idx`).
Doc blocks are still not searched. §3.1 measures the search that also covers assumptions, rules
and notes.

### 3.1 work_search over items, assumptions, rules and notes (search-scope)

Measured 2026-10-27 by aw-m34 (backlog/search-scope), branch `ws/search-scope` off `feat/workstate`
@ 9fb9afc, same seed and method. `WorkStateQueries.SearchAsync` is now a `UNION ALL` of four
branches (item, assumption, active constraint_rule, note), ranked by one `ts_rank`. With a scope,
each branch keeps only rows inside the subtree. `ts_headline` runs after the `LIMIT`.

| query | scope | first union ms | after ms | verdict |
|---|---|---:|---:|---|
| SearchAsync `flaky migration` | workspace | 21.6 | 2.16 | **index** + **rewrite** |
| SearchAsync `frontier rollup planner` | workspace | 18.8 | 1.46 | **index** + **rewrite** |
| SearchAsync `flaky migration` | run (`autowork/m1`) | 4.6 | 1.24 | **index** + **rewrite** |
| SearchAsync `flaky migration` | project (`autowork`) | 8.3 | 1.89 | **index** + **rewrite** |
| SearchAsync items only (refusal "nearby" suggestions) | workspace | 2.4 | 0.35 | **rewrite** |

- **Seq scan on `note` with per-row `to_tsvector`.** The first union built every note's vector on
  each search: `Seq Scan on note` (900 rows) under a Join Filter, 16.8 of the 21.6 ms. `note` is the
  thread table that grows with the run, so this is the "hot, large table" case in §1. **Index:**
  migration `NoteSearchIndex`, `note_search_idx ON note USING gin (to_tsvector('english', body))`.
  It is an expression index, so it lives in raw SQL in the migration, not in the EF model, and
  `Down` drops it. The note branch matches on exactly that expression and adds the weight only for
  ranking. After: `Bitmap Index Scan using note_search_idx`, 23 rows. `NoteSearchIndexMigrationTests`
  covers Up/Down/Up over live rows and the planner's use of the index.
- **`ts_headline` for every match.** Over a single table, Postgres postpones the expensive target
  list until after Sort/Limit. Inside a union it does not, so the item branch headlined all 137
  matches (2.6 ms). **Rewrite:** the union returns the text, and the outer query headlines only the
  `LIMIT` rows.
- **Left in place:** `Seq Scan on assumption` (156 rows) and `on constraint_rule` (60 rows), each
  vectorised per query, together about 0.8 ms. Both tables are small and grow by hand, one row per
  `work_assume` / `work_constrain`. An index is not warranted yet.

### Top plan nodes, before → after

**FactsAsync**, project scope, 51 ids, 66.7 ms → 1.2 ms:

```
before  Merge Join  rows=51  time=56 ms
          Aggregate  rows=4443  time=18.8 ms                  ← item_rollup for (nearly) every item
            Bitmap Heap Scan on item  loops=4444  (item_subtree_idx)
          Seq Scan [SubPlan 1] on item  loops=25 removed=3454 ← RefShared: lower(ref) = lower(i.ref)
after   Nested Loop  rows=51  time=1.1 ms
          Index Scan [SubPlan 1] on item using item_ref_lower_idx  loops=51
```

**ApiQueries.BoardAsync**, project scope, 1,367 rows, 77.1 ms → 31.5 ms:

```
before  Sort  cost=…  rows=1367  time=76.9 ms  JIT 34.8 ms
          Hash Join (p.id = i.id)
            Aggregate  rows=4955  time=21.7 ms               ← the view, for all 4,955 items
          Seq Scan on item_kind  rows=11  time=29.8 ms       ← JIT compile, charged to the first node
after   Sort  cost=298812  rows=1367  time=28.5 ms  JIT 0
          Aggregate (per row)  loops=1367  Bitmap Heap Scan using item_subtree_idx
```

**ResolveAsync** `autowork/m1/3.4`, 14.1 ms → 0.14 ms:

```
before  Seq Scan on item  removed=4951  Filter: workspace_id = … AND lower(ref) = '3.4' AND EXISTS(SubPlan 1)
        (JIT 13.0 ms: the per-row EXISTS pushed the estimate past jit_above_cost)
after   Index Scan on item using item_ref_lower_idx  Index Cond: (workspace_id = …) AND (lower(ref) = '3.4')
```

**SubtreeAsync**, project, 44.7 ms → 15.3 ms: the same full-view `Aggregate rows=4955` as Board
(~22 ms). After the rewrite it is a per-row rollup over `item_subtree_idx`.

**DefaultScopeAsync**, workspace, 20.6 ms → 3.7 ms: the same full-view aggregate, joined to the 28
runs and projects. After the rewrite, only those 28 rows are rolled up.

## 4. What was wrong, and the fixes

1. **`item_rollup` joined row by row aggregates the whole store.** The view (RollupReview migration)
   is `item p LEFT JOIN item c ON c.ancestors @> ARRAY[p.id] GROUP BY p.id`. `RollupAsync` filters
   it on a constant (`WHERE id = @item`) and is fine. A *join* on `r.id = i.id` cannot be pushed into
   the GROUP BY, so Postgres aggregates all 4,955 items (4,955 GIN probes, about 20 ms) and only then
   joins. **Rewrite:** `WorkStateQueries.Rollup`, a `CROSS JOIN LATERAL` count over `i`'s own subtree,
   with the view's five buckets. It is used by SubtreeAsync, ApiQueries.BoardAsync,
   BoardQueries.FactsAsync and DefaultScopeAsync. The view and `RollupAsync` are unchanged.
   **Same results, proven:** the opt-in test runs the fc45f08 SQL of all four queries
   (`RollupEquivalence.cs`, kept test-only) beside the live methods, for every run and project scope
   of the seed. It asserts identical rows in identical order. FactsAsync has no `ORDER BY`, so its
   rows are compared sorted by id. 9,662 subtree rows, 9,662 board rows, 9,662 facts rows and 29
   default scopes matched. Red-checked: counting `blocked` in the `review` bucket fails it at the
   first scope.
2. **No index serves `lower(ref)`.** Every `ResolveAsync`, the resolver behind every scoped tool call,
   seq-scanned `item`. So did FactsAsync's `RefShared` (once per row), and so does the same predicate
   in ApiQueries.RefsAsync. **Index:** migration `RefLookupIndex`,
   `item_ref_lower_idx ON item (workspace_id, lower(ref))`. It is an expression index, so it is
   raw SQL in the migration and not in the EF model; the snapshot is untouched. `Down` drops it.
   `RefLookupIndexMigrationTests` covers Up/Down/Up over live rows. Red-checked: with the index
   dropped after migrating, all four resolve forms go back to `Seq Scan on item` (4,955 rows,
   1.9–15 ms).
3. **JIT on a short-query workload.** Postgres 17 ships with `jit=on`, `jit_above_cost=100000`. The
   per-row laterals (document path, rollup, criteria, commit, verification) are cheap in fact but
   estimated at about 200 per row. Any subtree over about 500 rows crosses the threshold and pays
   about 35 ms of LLVM compilation. Board was 27 ms of work plus 35 ms of compile. **Setting:**
   `WorkStateDataSource.WithSessionSettings` adds `Options=-c jit=off` to every connection the server
   opens: the Dapper data source *and* the EF context (`UseWorkState`), so it is one setting. It is
   per session, so the Postgres instance's default is unchanged (`SessionSettingsTests` asserts both
   sides). Red-checked: without the option, both assertions fail, and Board goes back to 51–70 ms
   with 37 ms of JIT. Decided by the controller (skills-9a), 2026-10-27.

## 5. Seq scans left in place, and why

The *after* plans still contain these seq scans. Each one is a planner choice where an index path
exists, not a missing index, and none is near budget:

- **Frontier**: `item` (status filter) and `item_link` (`type='blocks'`) are hash-join build sides
  for the two NOT EXISTS anti-joins. `item_link_to_idx` and `item_sibling_order` exist, and the run
  plan uses them for the child check. A hash join over a 5k table is cheaper than 700 index probes.
  6–10 ms.
- **Threads / UnconsumedNotes**: `item` as the hash side of the note → item join (about 3 ms). `note`
  and `assumption` are tiny, and their partial indexes (`note_pending_idx`, `assumption_open_idx`)
  are used where selective.
- **Board**: hashed EXISTS subplans scan `verification` (passed+orchestrator, 0.6 ms) and `event`
  (`kind='forced_transition'`, 20,000 rows, about 1.8 ms) once per query instead of probing per row.
  **Watch this one:** it grows linearly with the journal, the only unbounded table. At about 10× this
  journal it would be about 20 ms of Board. The fix then is a partial index
  `event (item_id) WHERE kind = 'forced_transition'`. It is not added now: 1.8 ms is not a problem
  yet, and the brief rules out speculative indexes.
- `item_kind` (11 rows) and `claim` (1.2k rows, expired-claims, <1 ms) are trivially small.

## 6. Re-running

Run the command in §1. The summary table prints to the test output, and every plan (SQL, then an
indented node tree with cost, estimated and actual rows, loops, time, buffers and conditions) goes to
`mcp/WorkState.Tests/bin/Debug/net10.0/explain-5k.txt`. The test fails if any measured query captured
no plan, or if a rewritten query's rows differ from its `item_rollup` form.

## 7. Row-level security (m54)

Measured 2026-10-28 by ws-tenant-rls, branch `unit/tenant-rls`, postgres:17 in Docker Desktop on Windows.
Row-level security (doc 02 §15) puts every request-path read under the role `workstate_app` with a
policy on each tenant table. This section measures what that costs, and how two changes brought it back.

**Method.** `RlsPerfTests` (opt-in) calls the real query methods against an existing database, as the
owner and as the tenant role, interleaved call by call, 60 rounds after 3 warm-ups:

```
dotnet test mcp/WorkState.slnx --filter FullyQualifiedName~RlsPerfTests -e WORKSTATE_RLS_PERF="Host=…;Database=…"
```

With `-e WORKSTATE_RLS_PERF_SEED=1` the target must be an empty database: it is migrated and given the
§2 seed first. Two numbers per read:

- **server** — mean execution time per statement from `pg_stat_statements`, one row per role. A read
  measured at run and at project scope is one statement, so its mean covers both.
- **wall** — median per call as the client sees it, round trips included.

The owner path goes through the same helper and so is already a transaction. Against the owner, a
tenant read pays one extra round trip, the `set_config`. Against the code before m54, which ran a read
with no transaction, it pays two: the `set_config` and the closing `ROLLBACK`. Over Docker Desktop on
Windows a round trip is about **0.7 ms**. That is the whole wall delta on the small reads (rollup:
server +0.01 ms, wall +0.7 ms). **The gate is judged on server time**; the wall numbers are reported
because they are what a caller feels on this machine. Container-to-container in the deploy, a round
trip is a small fraction of that.

**The gate** (brief m54): frontier, rollup and board, live-data copy, with and without RLS; more than
20% → stop.

### 7.1 Live-data copy: as built, and the two fixes

`ws-final.dump` restored as the image's superuser: one workspace, 7 projects, 628 items. Server mean
in ms, owner → tenant role:

| read | as built | + tenant as InitPlan | + InitPlan + LEAKPROOF (3) | **shipped** (LEAKPROOF 4) |
|---|---|---|---|---|
| frontier | 0.928 → 1.690 | 0.916 → 0.981 | 0.897 → 0.945 | **0.919 → 0.974** (+6%) |
| rollup | 0.058 → 0.475 | 0.058 → 0.223 | 0.059 → 0.067 | **0.057 → 0.065** (+15%) |
| board | 3.09 → 37.58 | 3.07 → 16.39 | 3.04 → 2.16 | **2.97 → 2.16** (−27%) |
| subtree | 0.758 → 35.47 | 0.764 → 14.88 | 0.772 → 0.775 | **0.732 → 0.759** (+4%) |
| default scope | 1.11 → 21.26 | 1.11 → 9.14 | 1.12 → 1.14 | **1.09 → 1.14** (+5%) |
| threads | 1.56 → 1.88 | 1.57 → 1.60 | 1.52 → 1.59 | **1.51 → 1.55** (+3%) |
| journal head | 0.227 → 0.241 | 0.236 → 0.236 | 0.228 → 0.245 | **0.220 → 0.235** (+7%) |
| done-unverified | 0.241 → 0.444 | 0.253 → 0.204 | 0.239 → 0.236 | **0.239 → 0.233** (−3%) |
| expired claims | — | — | — | **0.221 → 0.294** (+33%) |
| stale in-progress | — | — | — | **0.014 → 0.195** |
| search (two words) | — | — | — | **8.58 → 8.66** (+1%) |

Wall median in ms, owner → tenant role:

| read | as built | + InitPlan | + InitPlan + LEAKPROOF (3) | **shipped** |
|---|---|---|---|---|
| frontier/run | 3.23 → 4.57 | 3.20 → 3.94 | 3.22 → 4.02 | **3.22 → 4.07** (+26%) |
| frontier/project | 3.88 → 5.33 | 3.79 → 4.48 | 3.79 → 4.58 | **3.66 → 4.50** (+23%) |
| rollup/run | 1.53 → 2.68 | 1.59 → 2.41 | 1.63 → 2.30 | **1.59 → 2.27** (+43%) |
| rollup/project | 1.53 → 2.65 | 1.63 → 2.35 | 1.62 → 2.38 | **1.54 → 2.24** (+46%) |
| board/run | 4.91 → 26.53 | 5.09 → 13.87 | 5.13 → 5.43 | **4.61 → 5.28** (+15%) |
| board/project | 6.99 → 56.30 | 7.24 → 27.28 | 7.30 → 6.68 | **6.35 → 6.46** (+2%) |
| subtree/run | 2.34 → 24.03 | 2.47 → 11.76 | 2.58 → 3.21 | **2.32 → 3.11** (+34%) |
| subtree/project | 2.94 → 52.97 | 3.06 → 24.10 | 3.15 → 3.81 | **2.97 → 3.73** (+26%) |
| default scope | 3.34 → 24.58 | 3.29 → 12.35 | 3.26 → 3.97 | **3.12 → 3.88** (+24%) |
| threads | 3.93 → 5.12 | 3.96 → 4.86 | 4.05 → 4.87 | **3.77 → 4.58** (+22%) |
| done-unverified | 2.11 → 3.05 | 2.15 → 2.78 | 2.18 → 2.96 | **2.06 → 2.81** (+36%) |
| expired claims | — | — | — | **2.31 → 3.35** (+45%) |
| stale in-progress | — | — | — | **1.71 → 2.74** (+60%) |
| search (two words) | — | — | — | **11.36 → 12.33** (+9%) |
| journal head | 1.80 → 2.45 | 1.85 → 2.50 | 1.91 → 2.63 | **1.91 → 2.62** (+37%) |

**As built, the gate failed by an order of magnitude.** Under a policy, Postgres uses a query's own
condition as an index condition only if its function is `LEAKPROOF`. `arraycontains` (`ancestors @>`),
`lower` and the text-search match are not. So `item_subtree_idx`, `item_ref_lower_idx` and
`item_search_idx` went unused, and each subtree count scanned the workspace. The board's and the
subtree's per-row rollup laterals (§4) made that quadratic. `EXPLAIN` as the role showed
`Seq Scan on item … Filter: workspace_id = … AND ancestors @> …` where the owner gets a
`Bitmap Index Scan on item_subtree_idx`.

Two fixes, both in migration `TenantRls`:

1. **The tenant as a scalar subquery.** `workspace_id = (SELECT NULLIF(current_setting(…), '')::uuid)`
   is evaluated once per statement as an InitPlan, not once per row. It brings frontier and threads to
   par and halves the rest, but cannot give the indexes back.
2. **LEAKPROOF** on `arraycontains(anyarray, anyarray)`, `lower(text)`, `ts_match_vq(tsvector, tsquery)`
   and `to_tsvector(regconfig, text)`. The fourth serves `note_search_idx`. It was found on the 5k seed
   (§7.2): without it, search took 20.6 ms instead of 2.2 ms. Why this is safe for WorkState, and what it
   needs, is in doc 02 §15.

**Verdict on server time: passes.** Frontier +6%, rollup +15%, board −27%. Every other read measured is
within 20% as well, except expired claims (+33%, +0.07 ms) and stale in-progress (+0.18 ms on 0.01 ms).
The wall deltas are the 0.7 ms round trip.

### 7.2 The §2 seed (4,955 items), same method

Measured with `WORKSTATE_RLS_PERF_SEED=1` into an empty database, so each statement is compared within
one database. Server mean in ms, owner → tenant role, shipped migration:

| read | server | Δ | wall median | Δ |
|---|---|---|---|---|
| frontier | 7.57 → 7.27 | −4% | run 8.86 → 9.25, project 11.83 → 12.55 | +4%, +6% |
| rollup | 0.391 → 0.434 | +11% | run 1.95 → 2.68, project 2.03 → 2.78 | +38%, +37% |
| board | 21.79 → 24.27 | +11% | run 17.88 → 20.99, project 31.61 → 35.87 | +17%, +13% |
| subtree | 9.83 → 10.18 | +4% | run 8.64 → 9.60, project 15.47 → 16.60 | +11%, +7% |
| default scope | 3.55 → 3.94 | +11% | 5.76 → 7.04 | +22% |
| threads | 2.60 → 2.78 | +7% | 4.86 → 5.77 | +19% |
| journal head | 0.059 → 0.062 | +4% | 1.75 → 2.41 | +37% |
| search (two words) | 2.20 → 3.14 | **+43%** | 4.98 → 6.71 | +35% |
| done-unverified | 1.12 → 2.20 | **+97%** | 3.09 → 4.92 | +59% |
| stale in-progress | 0.347 → 0.737 | **+112%** | 2.12 → 3.46 | +63% |
| expired claims | 0.706 → 2.136 | **+202%** | 2.83 → 5.16 | +83% |

**The gate's reads pass here too: frontier −4%, rollup +11%, board +11%.** Four reads outside the gate
do not. Each is under 2.2 ms at this size, and each is listed as an open item:

- **Expired claims.** `claim` has no workspace column of its own, so its policy goes through `item`.
  The planner turns that into a hashed subplan over every item of the workspace (4,955 rows), built
  before the claim filter. The owner's plan instead hash-joins the claims onto the subtree it scanned
  through `item_subtree_idx`. A direct policy needs `claim.key_workspace_id` made `NOT NULL`, or a
  workspace column: a schema change, not made here.
- **Done-unverified, stale in-progress.** Same indexes as the owner, but the planner cannot see the
  InitPlan's value. It sizes `workspace_id = (InitPlan)` as an average workspace (1 / `n_distinct`). It
  estimated 1 row where 565 came back, so it chose nested loops. The same average is a risk wherever
  tenants differ widely in size: a large tenant is planned as a small one. It is also why the plan
  guard in `RlsTests` runs in a database of its own, with one workspace. In the shared test database,
  the average workspace is a few rows, and a scan by workspace wins on cost alone.
- **Search.** The item and note branches use their indexes again. What is left is the per-branch policy
  filter and the joins to `item` for the scope of each assumption, rule and note.

`QueryPlanTests` measures as the tenant role with `-e WORKSTATE_EXPLAIN_RLS=1`, and drops the
`set_config` statements from its plans. Its run-to-run noise between two containers is too large for a
20% comparison: board/run measured +19% in one pair of runs and +28% in the next. Use it for plans, not
for deltas. Under the role, its rollup equivalence check still found every row identical: 9,662
subtree, board and facts rows, and 29 default scopes.

## 8. Project access (m59)

Measured 2026-10-29 by ws-project-access, branch `unit/project-access`, on the live-data copy restored
from `pre-1.0.1.dump` (one workspace, 7 projects, 773 items; 4 users, 3 of them members of all 7 through
the ProjectMembers seed and one of none), postgres:17 in Docker Desktop on Windows. Doc 02 §15b puts a second, restrictive
policy, `project_access`, on every table whose rows belong to a project. This section measures what that
costs and how the shipped design came to be.

**Method.** `ProjectAccessPerfTests` (opt-in) calls §7's reads as the owner, as a static token (the tenant
role with no user) and as a member (the tenant role as the user who is a member of the most projects),
60 rounds after 3 warm-ups each. The static token and the member run as the same role, so each mode is a
pass of its own, with `pg_stat_statements` reset before it and read after it:

```
dotnet test mcp/WorkState.slnx --filter FullyQualifiedName~ProjectAccessPerfTests -e WORKSTATE_PROJECT_ACCESS_PERF="Host=…;Database=…"
```

The 1.0.1 baseline is the same database with the twelve `project_access` policies dropped, so reads run
under §7's tenant policies alone. The gate (brief m59): board, frontier and rollup, server mean, member,
at most 20% over 1.0.1.

### 8.1 Three designs

Server mean in ms, member, 1.0.1 → design:

| read | function per statement | `app.projects` per transaction | **shipped: `access_seq`** (median of 3 pairs) |
|---|---|---|---|
| board | 4.69 → 7.71 (+65%) | 4.69 → 5.75–6.12 (+23–30%) | **6.09 → 6.01 (−1%)** |
| frontier | 1.75 → 3.01 (+72%) | 1.75 → 1.69–1.76 (−1 to +1%) | **2.17 → 1.98 (−9%)** |
| rollup | 0.134 → 0.528 (+294%) | 0.134 → 0.164–0.183 (+23–37%) | **0.222 → 0.245 (+10%)** |
| subtree | 1.62 → 2.25 (+39%) | flat | **2.08 → 1.79 (−14%)** |

1. **The caller's hidden projects from a function, once per statement.** `(SELECT
   workstate_hidden_projects())` is an InitPlan, but every table a statement reads carries its own, and a
   PL/pgSQL or SQL function call costs 0.1–0.2 ms of its own (a SQL function's body is planned per call).
   The rollup reads `item` twice and paid two calls; the board paid dozens. A PL/pgSQL version, whose plan
   is cached per session, brought the board to +38%.
2. **The roots the user may see, set once per transaction (`app.projects`).** The policy compares each
   row's root with that array and asks the live function only for a root outside it. Two costs were left:
   the comparison on every item row a read touches — about 2,000 on the board, across its laterals — and
   the item lookup behind every child row the board reads (`note`, `verification`, `event`). A board's
   unconsumed-notes check is planned as one scan of the workspace's pending notes, and their policy turned
   into a hashed scan of all 773 items. The cast of the setting had to move inside its InitPlan: outside,
   the text was parsed into a `uuid[]` once per row and the board took 17 ms.
3. **Shipped: `access_seq`.** The answer is read once per transaction — the seq and the projects hidden
   from the user, from one snapshot — and trusted while the statement still reads the same seq. A member
   of every project then pays nothing per row: no comparison against an empty list, and no item lookup
   behind a child row. Any change moves the seq and puts the policy back on the live check (doc 02 §15b).

### 8.2 The shipped design, three alternating pairs

Server mean in ms per statement: three runs of each, alternated (m59, then 1.0.1, three times), because
the machine's load moved between runs — the owner's board ranged 6.97–9.30 ms across them.

| read | mode | 1.0.1 | m59 | median Δ |
|---|---|---|---|---|
| board | member | 5.147 / 6.093 / 7.459 | 5.863 / 6.007 / 8.134 | **−1%** |
| board | static | 5.346 / 5.441 / 8.567 | 5.500 / 6.245 / 6.162 | +13% |
| frontier | member | 1.886 / 2.174 / 2.339 | 1.919 / 1.981 / 2.254 | **−9%** |
| frontier | static | 1.950 / 1.999 / 2.801 | 1.858 / 1.905 / 1.903 | −5% |
| rollup | member | 0.172 / 0.222 / 0.263 | 0.234 / 0.245 / 0.251 | **+10%** |
| rollup | static | 0.192 / 0.193 / 0.288 | 0.203 / 0.221 / 0.211 | +9% |
| subtree | member | 1.806 / 2.075 / 2.190 | 1.786 / 1.785 / 1.814 | −14% |
| subtree | static | 1.856 / 1.926 / 2.337 | 1.686 / 1.820 / 1.720 | −11% |
| default scope | member | 1.533 / 1.875 / 1.993 | 1.905 / 1.919 / 1.952 | +2% |
| projects | member | 2.579 / 2.905 / 3.207 | 2.990 / 3.104 / 3.411 | +7% |
| threads | member | 2.096 / 2.622 / 2.545 | 2.465 / 2.350 / 2.478 | −3% |
| search | member | 9.422 / 10.564 / 11.583 | 10.133 / 9.746 / 11.696 | −4% |
| journal head | member | 0.193 / 0.258 / 0.241 | 0.233 / 0.228 / 0.253 | −3% |

**Verdict on server time: passes.** Every read is within 20% for the member and the static token, and the
gate's three are −1% (board), −9% (frontier) and +10% (rollup). The owner's own numbers moved by up to
±15% between runs, so a single run's delta below that says little. Besides the reads, a transaction whose
request has a user runs `workstate_project_access` inside the `set_config` statement the tenant helper
already sends: 0.15 ms of server time per transaction, and no round trip.

**What it does not measure.** A non-member — a user with a project hidden from them — still pays the
comparison against the hidden list on each item row, and an item lookup behind each child row of a read.
That path is the correct one, not the fast one, and was not part of the gate. Nor was a transaction that
spans a membership change: from that change on, its statements run the live check.
