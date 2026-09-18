# Ledger — read-tools-and-block-citations

> Recovery payload for a fresh orchestrator. Header block is kept current; the task log is a table.

## Status
- **Phase:** COMPLETE — all four phases shipped, gated, and closed out
- **Current task:** none — run finished
- **Next action:** none. Branch `feat/read-tools-and-block-citations` is ready for the owner to review and push; nothing is pushed.
- **Branch:** `feat/read-tools-and-block-citations` off `7f28c5457` ·
  **Worktree:** `.claude/worktrees/read-tools-and-block-citations` · **Last commit:** `15058a848` · 16 commits, 31 files, +4036/-206
- **Mode:** full
- **Baseline, measured properly at last:** build 0 errors / 21 warnings. **Full suite at base
  `7f28c5457`: 1 failed / 1567 passed. Full suite at HEAD: 1 failed / 1767 passed** — the same single
  failure, `ConnectorFlowTests.The_issuer_suffixed_metadata_path_is_not_served`. ~200 tests added, none
  failing. Do not chase it.
- **`Euwg.DocumentFetchTransientTests(status: 504)` is a dice roll, not a regression.** It binds
  `Random.Shared.Next(20000, 60000)` and calls `HttpListener.Start()` once with no retry; this machine
  reserves TCP ranges inside that window (49693-49992 and more). Failed 2 runs, passed 3. **Do not
  investigate; do not "fix" it as part of this run.**
- **Last plan audit:** after Phase 2, over Phases 3-4 — **not-ready**, 6 blockers; re-audited after the
  fix round — **not-ready again, 5 NEW defects from the fix itself**; both rounds now fixed and
  mechanically verified, with a narrow confirmation outstanding on two non-mechanical edits ·
  **Next due:** not again this run (≤2 small phases remain)

## Open assumptions
| # | Assumption | Evidence level | Reversal cost | Revisit when |
|---|---|---|---|---|
| ~~A1~~ | ~~`judgment_sections.judgment_id` is indexed~~ | **CONFIRMED** 1.0: `ix_judgment_sections_judgment_id`, `20260909160942_Initial.cs:1340-1343` + snapshot `:1239`. No GIN index exists anywhere. | — | closed |
| A2 | Materialising `files.content` per read call is acceptable | reasoned; one TOAST read per call | low: Phase B supersedes | Phase B |
| A3 | HTML tags in `judgment_sections.text` add harmless tsvector noise | reasoned; tags are not Italian words | low: strip before ranking at a fetch cost | if 2.1 snippets come back noisy |
| ~~A4~~ | ~~old/new grammars coexist without ordering hazard~~ | **CONFIRMED** 1.3 by test: `DocumentCitationReslover.CanResolve` false for `doc12p3`/`doc12p3b7`, `JudgmentCitationReslover` false for `prov9s2`/`prov9s2b1`; old ids still resolve | — | closed |

## Carry into later briefs
- **4.1 now has an observable symptom** (Phase 3 gate finding 4): because read and search no longer share
  a source, the page-blind chunk prune lets search emit `doc{id}p37` inside `total-pages="10"`, which the
  read then refuses. Reachability is low — the retry path re-parses only when `Content` is empty — but the
  defect is no longer invisible.
- **The wiring of `CaseToolService`/`ChatFileToolService` is unverified** (gate test-strength): all 43
  Phase 3 tests still pass if `.Take(take + 1)` becomes `.Take(take)`, if the wrong column is projected,
  or if the `take` clamp is dropped. Closure is a `ToQueryString()` assertion — the same technique 4.1
  must use, since no test may touch a database.

## Carry into later briefs (earlier)
- **`ToolOutputBudget.PartialNotice` now takes a `PartialReason`** (`Budget`/`Limit`/`More`) — SPEC §5's
  API block records the real shape as of `bc5662dab`. Pick the true one; never pass Budget for a
  caller-limit cut.
- **Phase 3's shared service has TWO intakes** (SPEC §7.0): materialised `content` for the read path,
  already-ranked chunk rows for the search path. A content-only surface is the trap that pushes 3.2's
  grouping and header-strip back into both MCP tools.
- **Phase 2/3 — the emitter contract (SPEC §5).** Never emit a coordinate the resolver refuses (empty /
  whitespace / `NO_CONTENT_HERE` units now resolve to null in **both** corpora), and convert once on
  both sides (`JudgmentSectionCitationReslover.ToText`, `DocumentMarkdown.Pages`/`Blocks`). Emitter and
  resolver are one contract.
- **2.2 adds the ordered section-window query.** 2.1 deliberately added none — an unused method is dead
  code — so `JudgmentSectionService` has `Header`/`CountMatches`/`Rank` and 2.2 adds `Window`. It reuses
  `Blocks` / `RenderSection` / `Fit` unchanged.
- **2.2's resume pointer must be a `Sort`, not a position.** Sections the resolver would refuse are
  skipped, so "next `sectionFrom`" computed from the emitted list is off by however many were skipped —
  the same off-by-one class as 1.2's discrimination #1.
- **Phase 2 — emit `s{Sort}` from the raw column.** Not `Id`, not a position in a filtered list:
  `JudgmentService.cs:86-96` drops blank sections and exposes `Id` but never `Sort`.
- **Phase 2/3:** a cut that is **not** a budget cut (a `.Take(take)` on search hits, a section limit)
  produces **no** `<partial>` notice unless the tool routes it through `Fit` or constructs a
  `BudgetFit`. Surfaced by 1.2. Now recorded in SPEC §5; every 2.x/3.x brief must say it.
- **Phase 2/3 tests:** `BudgetFit` is a reference record — compare members, never whole instances.

## Deferred follow-ups
- [ ] **Security, pre-existing, widened by this run.** `DocumentBlockCitationReslover.Resolve` filters
      `TenantId` only — no case membership, no chat visibility — and sits on the live resolve path for any id
      the model emits. `doc{id}p{n}` is guessable, so a chat scoped to case A can surface a full page of a
      case-B document as a resolving citation. The chunk-level version of this hole already existed; this run
      widened it from one chunk to a page. Belongs with the case-membership hardening on
      `feat/security-idor-invite-checkout`. **Not changed here** — a close-out task with no gate behind it is
      the wrong place for it.
- [ ] **`total-pages` still has two sources when Extract returns no page list.** `FileParser.cs:108-110`
      keeps `parse.PageCount` on the `Markdown`-only path, so a DOCX with a reported 14 pages and no
      separators is `total-pages="14"` in search and `1` in read. The read's refusal names the true count, so
      the model recovers; one shared source would be better.
- [ ] **Search snippets are not quotable and now say so — but they are also not block-addressable.** The
      judgment side has no such asymmetry (`CercaInSentenza` returns real blocks). Giving document search
      block coordinates would need the text re-derived from `files.content`, i.e. Phase B.
- [ ] **`FileIndexer.IndexFile` itself is untested** (needs a DB): a mutant recording a constant page would
      pass the 18 prune tests.
- [ ] **The empty-survivor guard makes one case permanent.** A re-index yielding zero chunks that escapes the
      exact-equality early return now prunes nothing and marks the file Indexed, so a previous run's chunks
      stay searchable forever; the old code purged them. Reachable only via a re-parse, which today fires only
      when `Content` is empty. The precise fix distinguishes *why* nothing survived.
- [ ] **`ListCaseDocuments(metadata: true)` and the library tools are still unbounded** — no budget, no notice.
- [ ] **`Euwg.DocumentFetchTransientTests` is flaky by construction**: `Random.Shared.Next(20000, 60000)` +
      `HttpListener.Start()` with no retry, against a machine that reserves ranges inside that window. Will
      redden CI intermittently on any branch.
- [ ] **`ConnectorFlowTests.The_issuer_suffixed_metadata_path_is_not_served` fails in any worktree without a
      frontend build** — it wants `wwwroot/index.html`, which only `pnpm build` produces. Fails identically at
      base. Not ours, but it makes "full suite green" unachievable as a close-out criterion.
- [ ] Block-level highlighting / scroll-to-block in the document viewer — needs `char_start`/`char_len`,
      which arrives with Phase B. Phase A URLs are page-level by design (SPEC §3).
- [ ] `PHASE-B-file-blocks.md` — `file_blocks` replaces `file_chunks`. Owner: ship Phase A, then wait.
- [ ] When `feat/model-upgrade` merges: `CitationGate` must refuse a `Verified` verdict for a quote
      matched against massima content (`prov{id}`), now that `prov{id}s{sort}` exists to match against
      instead. Not this run — the gate is unmerged.

## Retro
Filed: **r-Larch/skills#10** — `retro: references/decompose.md#plan-md — a filter matching zero tests exits 0`.
The rule's own remedy ("write the filter against a class the task will *create*") is what produces a
zero-match filter, and `dotnet test --filter` exits 0 on no-match, so following the rule yields a decorative
verification. +38 tokens against a re-plan.

Second observation, **not filed** (one finding per run): `SKILL.md` › *Verification* says "Never re-confirm
an already-green state", which licensed me to carry a Phase 1 full-suite measurement through Phase 2 — after
Phase 2 had added two MCP tools, which is exactly what a schema snapshot test catches. I fed "exactly one
failure" to two gate briefs as an established fact; it was wrong by then. The sentence wants "… unless you
have since changed what it measures". Closest existing issue is #8 (`SKILL.md#the-phase-gate`), different
section.

## Orchestrator edits
- **SPEC §5 corrected after 1.0** (zero behaviour, doc only): the spec told a worker to wrap
  `TakeMaxChunks` from `CitationExtensions`. It lives in `LanguageModelContextExtensions`, and it
  cannot report *where* it cut — which the `<partial>` notice needs. §5 now says the budget helper
  accumulates `CountTokens` itself. Left uncorrected, 1.2 would have been built on a false premise.
- **CONVENTIONS.md gained `Test conventions` and `Token counting`** from 1.0, so no brief restates them.

---

## Task log

| Task | Verdict | Commit | Verify | Note |
|---|---|---|---|---|
| 1.0 recon | ACCEPTED | n/a (read-only) | n/a | A1 confirmed; SPEC §5 found wrong and fixed |
| 1.1 DocumentBlocks | ACCEPTED | `7e345e7f0` | `--filter ~DocumentBlocksTests` 35/35, build 0 errors | review skipped — see below; F3 confirmed |
| 1.2 ToolOutputBudget | ACCEPTED | `20fe37c7c` | `--filter ~ToolOutputBudgetTests` 17/17, build 0 errors | review skipped — additive-only, 0 tracked files modified; 7 impls defeated |
| 1.3 block resolvers | REWORKED → ACCEPTED | `cbeefd180` | `--filter ~BlockCitationResolverTests` 48/48, build 0 errors | A4 closed; HTML conversion corrected — see below |
| Phase 1 gate | PASS-WITH-MINORS | n/a | composition review over `7f28c5457..cbeefd180` | 3 minors → 1.4; re-verified → 1.5 |
| 1.4 + 1.5 gate fixes | ACCEPTED | `904fe32bf` | Phase 1 filters 120/120, build 0 errors | red-check contrast: count floor 4 failed vs membership-only **0** |
| 2.1 CercaInSentenza | ACCEPTED | `f2a34e387` | `--filter ~SentenzaToolTests` 24/24, build 0 errors | review → Phase 2 gate; 6/6 impls defeated; two deviations accepted, see below |
| 2.2 LeggiSentenza | ACCEPTED | `4dc9a0aa2` | `--filter ~SentenzaToolTests` 30/30, build 0 errors | resume pointer observed from a probe row, not computed; 7/7 defeated |
| 2.3 TrovaSentenze label+cap | ACCEPTED | `de860a9c3` | `--filter ~SentenzaToolTests` 36/36, build 0 errors | 6/6 defeated; renders its own `<source>` rather than capping via ToCitationContext |
| Phase 2 gate | PASS-WITH-MINORS | n/a | composition review over `904fe32bf..de860a9c3` | round trip byte-identical, block numbering correct; 3 minors + 1 tautological test → 2.4 |
| Plan audit ×3 | RE-PLANNED | n/a | over unexecuted Phases 3-4 | 6 blockers, then 5 new defects from my fix, then 1 more — see below |
| 2.4 gate fixes | ACCEPTED | `bc5662dab` | `~SentenzaToolTests\|~ToolOutputBudgetTests` 54/54 strict-flagged | `PartialNotice` gained a reason; single-caller claim on `MassimeSearchHandler` verified by me |
| 3.0 DocumentSectionService | ACCEPTED | `292e32a93` | `~DocumentSectionServiceTests` 25/25 | static, no DbContext — the "owns no query" boundary made structural; 7/7 defeated |
| 3.1 read from content | ACCEPTED | `2b3decb8c` | `~DocumentReadToolTests` 10/10 | **auth verified by me: no predicate line moved**; chunk page count deleted; 4/6 defeated, 2 honestly inspection-only |
| 3.2 search page coords | ACCEPTED | `72a769ed8` | `~DocumentSearchToolTests` 8/8 | probe-row cut; `files.Pages` for the count; 6/6 defeated |
| 3.3 snapshot review | ACCEPTED | `23f5d2e4d` | `~McpSchemaTests\|~McpTests` 20/20 | 29→31 tools, nothing renamed, **no parameter moved**; received files regenerated before diffing |
| Phase 3 gate | PASS-WITH-MINORS | n/a | composition review over `bc5662dab..23f5d2e4d` | 5 minors + a test-strength gap → 3.4 and 3.5 |
| 3.4 output correctness | ACCEPTED | `b688cd01d` | 103/103 across Phases 2-3 | cut moved **before** the tags; red-check 5 failed incl. 2 real XmlException |
| 3.5 pins + judgment cut | ACCEPTED | `0c638f9ce` | 126/126; full suite 1786/1 | 4 query-shape pins, each proven to fail on its revert (2/1/1/2) |
| 4.1 chunk prune | ACCEPTED | `fd3d153f3` | `~FileIndexerPruneTests` 18/18 | **review: pass-with-minors**; `!keys.Any(...)` does not translate — packed key instead |
| Final review | DELIVERS-WITH-GAPS | n/a | whole-run review against the goal paragraph | **3 BLOCKERs four gates missed** → 4.2 |
| 4.2 honesty fixes | ACCEPTED | `15058a848` | 189/189 filtered; full suite 1807/1 | massima title, snippet label, and the emit↔resolve round trip |

Verdicts: ACCEPTED · REWORKED · RE-PLANNED · ESCALATED · SKIPPED

---

## Exceptions

### 1.1 — review skipped, and the boundary of what the coordinate guarantees
Review skipped deliberately although the block rule *is* an identity rule (normally review-required).
Justification: the diff is **purely additive** (35 insertions, 0 deletions) with **no callers yet**, and
the worker's discrimination exercise is stronger evidence than a review would be — it swapped 8 wrong
implementations into place and confirmed each fails the fixture (`RemoveEmptyEntries` → 4 failed,
0-based block numbering → 18, collapsed internal newlines → 2, `\n\n`-only blank detection → 7, …).
The identity seam across 1.1/1.3 is what the **Phase 1 gate** exists to check; that is where it gets
reviewed, once, with both halves present.

**F3 confirmed against the code** (`FileIndexer.cs:34-42`): the indexer splits on the same separator
with `StringSplitOptions.None` and writes `Page = index + 1`, and a `NO_CONTENT_HERE` page returns `[]`
from inside `SelectMany((page, index) => …)` so its index still advances. Page N from `Pages()` is the
page N in `file_chunks.Page`.

**Boundary worth stating plainly** (from the worker's RISKS): blocks are stable against a **re-index**,
which is what this run set out to fix and what SPEC §3 claims. They are *not* stable against a
**re-parse** that changes whitespace — block numbering is a function of the page text. Re-parsing the
corpus is out of scope and unaffordable, so this holds in practice; it is recorded so nobody later
mistakes the guarantee for something broader. Page numbers are stable under both.

### Phase 3 gate — PASSED (pass-with-minors)
Seams 1 and 2, the two I was most worried about, both **hold**. `total-pages`' two sources agree by
construction for any servable file: `files.Pages` and `files.content` are written in the same `SetParsed`
(its only writer), `content` is `parse.Pages` joined with the very constant the reader splits on, and the
Extract client contract pins `PageCount == Pages.Length`. Page identity holds too — `file_chunks.Page` is
`index+1` of the same split, `NO_CONTENT_HERE` consumes its index, and both tools require
`FileStatus.Indexed`, which excludes the re-index window. Intakes, notice vocabulary, authorisation and
the dead-code residue all came back clean.

Five minors, ranked, and one matters more than its label. **A unit larger than the whole 25k budget is cut
by `TrimPrompt` mid-element**, so the model receives an unclosed `<page>`/`<b>`, and on the read path the
resume pointer then names the *next* page — leaving the unread remainder of the trimmed page reachable only
through search. Newly reachable in Phase 3: a read unit used to be one ≤1024-token chunk and search had no
budget at all; a unit is now a whole page (an XLSX sheet is one page) or a whole `<doc>`. Also: `got` states
`su {hits} estratti trovati` using the `take+1` probe count, so a 500-match search claims 26 — the one place
the seven-tool vocabulary says something false; `FileParser.cs:106` prefers the server's `PageCount` over
the count of pages it actually joined, the only line where the two totals could ever diverge; and the
"we don't have a better option" comment on two now-callerless `QueryPages` methods is now simply untrue.

Also surfaced: nothing drives the two MCP services, so the *wiring* is unpinned — see Carry into later
briefs. 3.4 takes the output-correctness findings, 3.5 the hygiene and the wiring pins.

### Plan audit — after Phase 2, over Phases 3-4. Two rounds, both not-ready.
**Round 1, six blockers.** The one that mattered was measured, not argued: `dotnet test --filter` naming
a class that does not exist prints "No test matches" and **exits 0**, so every remaining `Verify:` line
was decorative — a worker would have run it against unimplemented work, seen success, and I would have
confirmed the lie by running the same command. I re-measured: plain → exit 0, with
`-- RunConfiguration.TreatNoTestsAsError=true` → exit 1, real filter still exit 0 with its `Passed!`
line. (While measuring I read `$?` off the end of a pipe, where it belongs to `tail` — the exact trap the
skill names. Re-measured without the pipe.)

The other five: SPEC §5's "share the same function" invited reading *through*
`DocumentBlockCitationReslover`, whose filter is `TenantId` only, which would have dropped case
membership on every case document; Phase 3 commissioned no shared service, so the block rule would have
been written twice across two MCP services — §4's own stated defect; `PagesCount = Chunks.Max(Page)` is
chunk-derived and the indexer emits no chunk for a `NO_CONTENT_HERE` page, so a scanned tail makes real
pages unreachable; 4.1's red-check had no pure seam because the prune is `ExecuteDeleteAsync`; and
Phase 2's boxes were unticked, so an agent taking "the next unchecked task" would re-implement committed
code.

**Round 2, five NEW defects introduced by my fix round** — none a restatement. A new red line 7 forbade
`NOT EXISTS` outright and thereby made 4.1 unimplementable, since `!keys.Any(...)` is its only
translatable shape; SPEC gained a free-form `reason` that contradicted the committed `PartialNotice`
signature three lines below; 3.0's charter was closed but 3.1/3.2 did not list its file, so they would
duplicate or break red line 6; `DocumentSectionService` existed only in PLAN while SPEC is declared the
source of truth; and four stragglers (an uncommissioned `quotable="true"`, a Phase 3 gate floor lower
than one of its tasks, a 2.4 box I claimed to have added and had not, stale `:81`/`:75` in SPEC §8).

Both rounds fixed. Round 2's fixes were six mechanical edits I verified by grep plus two judgment calls
(red line 7's wording, the new SPEC §7.0) sent back for narrow confirmation rather than a third full
audit — a third pass is where the paralysis loop lives, and the findings were converging on different
axes each round, not circling one.

### 2.1 — two deviations accepted, both better than the brief
**tsvector over `Text` alone, not `Title + " " + Text`** as `MassimaExtensions` does: in Postgres a NULL
title NULLs the whole concatenation and the row drops out silently. The table's own commented-out
`JudgmentSectionExtensions` also reads `Text` alone. Cost: a query matching only a section *title*
("MOTIVI DELLA DECISIONE") will not rank — acceptable, those titles are boilerplate.

**The `<sentenza>` wrapper carries no `citeId`.** Deliberate and correct: the only id for a judgment as
a whole is `prov{id}`, which resolves to the massima — precisely the text that must not be quoted.
Omitting it means the model has no handle for "the judgment" and must cite a section.

Match rule: case-folded prefix containment, each term ≥4 chars cut to 8 and trailing vowels trimmed
(`contratto`→`contratt`, so `contrattuale` matches). Terms under 4 chars are dropped, so a 3-letter term
like "IVA" does not steer block selection — the first-block fallback covers it. Postgres still does the
real Italian stemming when ranking the section.

Open: `Header`/`CountMatches`/`Rank` are three round trips per call and no query plan was verified,
because no database may be touched in this run.

### Phase 1 gate — PASSED (pass-with-minors), two fix rounds
Seams checked: **resolver chain** — clean; `ArticleKey.IsValidKey` is `^act_(\d+|[^/]+)/(.*)`, needs an
`act_` prefix *and* an embedded `/`, so it cannot match any new grammar; **ten** implementations exist
(I said six, the gate first said eight), all registered, order irrelevant. **Emit/resolve symmetry** —
holds; the resolver's one extra `.Trim()` is numbering-neutral. **`Blocks()` on `ToMarkdown` output** —
verified by loading the real `LarchSys.Htmls` 1.3.2 assembly out-of-tree: `<p>`/`<div>`/`<br><br>`
separate with a blank line, lists/tables/blockquotes stay one block, no over-fragmentation.
**Identity three ways** and **lifecycle** — clean.

Three minors, all real, fixed in 1.4 and re-verified: a comment asserting a false provenance; an empty
page resolving to a *successful* empty citation; a seam test covering 2 of 10 resolvers. The
re-verification found finding 2 fixed on documents only — the identical defect survived on judgments,
on the "only the emitter protects us" reasoning that had just been rejected — and that the membership
floor could not catch its own regression. Both closed in 1.5.

Re-gated after fixes: yes, once (round 1 → round 2). Round 2 verified by me directly rather than a
third gate pass; the red-check contrast (4 vs 0) is stronger evidence than another review, and a third
round would be the paralysis loop.

### 1.3 — my brief named the wrong HTML helper, and the worker caught it
The brief sent the worker to `Html.StripHtmlTrimmed`. It removes tags and inserts nothing, so
`<p>a</p><p>b</p>` fuses across the boundary (measured: `"fondato.Secondo"`) and a whole section
collapses to **one** block — which would have made `prov{id}s{sort}b1` a synonym for the section and,
worse, produced text no real quotation could ever match. The worker measured both options and stopped
rather than choosing, because 2.1 is briefed on the same function and two conversions would disagree.

Corrected to `Html.Parse(text).ToMarkdown(removeLinks: true, htmlDecode: true)` — already applied to
**`judgment_sections.Text` specifically** at `MassimaAgent.cs:37` and `JudgmentSummaryHandler.cs:108`,
and to the analogous corpus at `ActicleCitationReslover.cs:42`. The resolver was the odd one out.
Red-check: swapping `StripHtmlTrimmed` back fails exactly the two new facts (2 failed / 46 passed).
SPEC §6.2 updated so 2.1 inherits the right function.

Worker deviation, accepted: the conversion is a named `internal static ToText` rather than inline, so
the tests bind the production path instead of asserting about the NuGet package — without it the
red-check could not fail on a resolver-side swap. Reaches `Nomos.Tests` via the existing
`InternalsVisibleTo`.

Review skipped for 1.3 (identity rule + tenancy would normally require one): additive-only, 0 tracked
files modified; A4 proven by test; 7 wrong implementations defeated; and I verified the tenant
predicate by hand — `_.Id == coordinate.SourceId && _.TenantId == tenantContext.TenantId`, identical to
`DocumentCitationReslover.cs:68`, with the judgment resolver correctly carrying none. The composition
seam goes to the Phase 1 gate.

### Phase 0 — framing
Plan authored from a first-hand read of the citation pipeline, the three MCP tools, the file ingestion
path and the judgment schema (this session, pre-orchestration). SPEC §2 lists the ten facts the design
rests on with `file:line` evidence; every one was read rather than assumed, except A1, which is an EF
convention inference and is the first thing task 1.0 settles.

Three owner corrections shaped the plan and are recorded here because they overrule the obvious design:
`Massima.VerbatimQuotes` is empty and unaffordable to backfill; no GIN index on `judgment_sections`
(rank one judgment at a time instead, add the index only if it is ever needed); `file_blocks` is
extracted to Phase B and must *replace* `file_chunks` rather than join it.
