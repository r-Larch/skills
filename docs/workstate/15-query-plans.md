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
