# 03 — Tech stack and build plan

Aligned to the **`nomos-service` blueprint** (the workspace's standard shape for a standalone .NET
service), because this *is* one: a focused backend the agents call over a wire contract, with an
operator dashboard.

---

## 1. Stack

### Service

| Layer | Choice | Why |
|---|---|---|
| Runtime | **.NET 10**, `net10.0`, `.slnx`, nullable + implicit usings on, one root `Directory.Build.props` | workspace standard |
| MCP | **`ModelContextProtocol.AspNetCore` 2.2.0** (+ `ModelContextProtocol.Core`) | official C# SDK; 2.2.0 aligns with the 2026‑07‑28 revision and negotiates down to 2025‑11‑25 |
| Transport | **Streamable HTTP**, stateless | 2026‑07‑28 removed protocol sessions; 2.2.0 defaults to stateless HTTP. No sticky routing, trivial horizontal scale |
| Data | **PostgreSQL 17+** via **Npgsql** | workspace substrate; `pg_trgm` + GIN, `jsonb`, `LISTEN/NOTIFY`, arrays |
| ORM | **EF Core 10** with `UseSnakeCaseNamingConvention()` for the write model; **raw SQL / Dapper for the recursive reads** | EF for migrations and the aggregate writes; hand-written SQL for the frontier, rollup and audit queries, which EF translates badly and which are the hot path |
| Migrations | EF Core migrations, applied deliberately (never `dotnet ef database update` against a design-time factory that resolves prod — the corpus has that exact incident) | |
| Observability | **OpenTelemetry** traces+metrics always *emitted*, exported only when an OTLP endpoint is configured; **Sentry** as an OTel backend gated on a DSN; correlation-id middleware **before** auth | standard non-negotiable #5 |
| Auth | **Static bearer token** (constant-time compare, fail-closed) on `/mcp`; **cookie session** on the App API; the two never cross | standard non-negotiable #2/#3. Extended here with per-agent scoped tokens (`agent_token`) |
| Errors | Typed `Results.Problem` mapping; MCP-side, `isError: true` with self-correcting text | standard #4 |
| Docs | OpenAPI scoped to the App API + Scalar at `/docs` | standard #8 |
| Container | Multi-stage Dockerfile (Node builds SPA → SDK publishes → aspnet runtime), `compose.yml` + `.env.example` + Traefik labels + external `web` network + `/health` healthcheck | standard #9 |

### Frontend (stack only — design deferred, per the brief)

Matched to `Nomos.Client/nomos` so the two are maintainable by the same hands:

| | Choice |
|---|---|
| Runtime / PM | **Node 24.7**, **pnpm** |
| Framework | **SolidJS 1.9** + **`@solidjs/router`** |
| Language | **TypeScript 7** (`tsc --noEmit` as the `pnpm ts` gate) |
| Build | **Vite 8** + `vite-plugin-solid`, output built into `wwwroot` via an opt-in MSBuild target |
| Styling | **Tailwind CSS 4** via `@tailwindcss/vite` (+ `@tailwindcss/postcss`), theme tokens as CSS variables |
| Components | **Kobalte** (`@kobalte/core` + `@kobalte/tailwindcss`) — headless, accessible |
| Icons | **solid-icons** |
| Live data | **`EventSource`** against the App API's SSE stream, fed by Postgres `LISTEN/NOTIFY` on `event.seq` |
| Charts (later) | `chart.js` + `solid-chartjs`, as in `Nomos.Client` |

No state library: Solid stores + a small resource layer over the App API, with the SSE cursor
(`event.seq`) driving invalidation. The board is a projection of the same views the MCP tools use —
**one query layer, two presentations.**

### Solution layout

```
WorkState.slnx                     net10.0
├─ WorkState.Abstractions/         DTOs + the wire contract. No dependencies. Published as NuGet
│                                  so a future Nomos/Gaia integration can resolve refs without
│                                  pulling the engine.
├─ WorkState.Core/                 domain, EF model, migrations, query services, rendering
│                                  (compact text + markdown). No ASP.NET.
├─ WorkState.Mcp/                  tool / prompt / resource classes. Depends on Core only.
├─ WorkState.Api/                  host: /mcp endpoint, App API, SSE, SPA static files,
│                                  auth, telemetry, load-shedding, Scalar
├─ WorkState.Tests/                xUnit, mirroring each layer
├─ frontend/                       SolidJS SPA → WorkState.Api/wwwroot
└─ tools/WorkState.Import/         markdown importer for the 181 existing files
```

**`WorkState.Core` owns the renderers.** The compact text form is domain logic, not presentation:
it is the primary product of every tool call, and it must be unit-tested for token density and for
never leaking ids the agent cannot use.

---

## 2. Hosting sketch

```csharp
builder.Services
    .AddMcpServer(o => { o.ServerInfo = new() { Name = "workstate", Version = "…" }; })
    .WithHttpTransport()
    .WithToolsFromAssembly()      // [McpServerToolType] classes in WorkState.Mcp
    .AddAuthorizationFilters()    // REQUIRED with ASP.NET Core; honours [Authorize] on tools
    .WithResourcesFromAssembly(); // fixed URIs only — templates are undiscoverable (doc 01 §8)
                                  // no WithPromptsFromAssembly: prompts are invisible (doc 01 §9)
                                  // no WithCompleteHandler:     no client mechanism (doc 01 §8)

app.MapMcp("/mcp").RequireAuthorization("agent-token");
app.MapGroup("/api").RequireAuthorization("dashboard-cookie");   // App API — excluded from OpenAPI
app.MapGet("/api/stream", SseHandler);                            // LISTEN/NOTIFY → EventSource
app.MapFallbackToFile("index.html");                              // SPA
```

> **Verified 2026-09-18** against the 2.2.0 assemblies (doc 01 §11) and against the live
> client (`mcp/PROBE.md`). Every symbol above is real, and the shape above is what U0 shipped.

Client registration:

```bash
claude mcp add --transport http workstate http://localhost:5290/mcp \
  --header "Authorization: Bearer $WORKSTATE_TOKEN"
```

---

## 3. Build plan

Phases are observable slices; each leaves the thing runnable. Tasks are sized per the `decompose`
rubric (one agent, end-to-end, one verification command that can go red).

### Phase 0 — Ground truth (no product code)

| | |
|---|---|
| 0.1 | ~~**Probe the SDK.**~~ **DONE 2026-09-18** — see doc 01 §11. Every assumption held; `AddAuthorizationFilters()` turned out to be a *better* mechanism than the design assumed, and `Stateless` is the framework default. No correction needed. (`WithListToolsHandler` looked useful here and was later dropped by task 0.2 — `[Authorize]` narrows the list unaided.) |
| 0.2 | ~~**Probe the client.**~~ **DONE 2026-09-18**, two rounds — see `mcp/PROBE.md` §5–6. Found that `structuredContent` **replaces** the text block (doc 01 §2 rewritten), prompts are **invisible** (§9 rewritten), completions have **no client mechanism** (§8), templates are readable but undiscoverable, and `[Authorize]` **narrows `tools/list` by itself** (§6 — `WithListToolsHandler` dropped). |
| 0.3 | Scaffold the solution, `Directory.Build.props`, CI, `.gitignore`, compose with Postgres 17 + `pg_trgm`. |

*Gate:* the client's real behaviour is written down, and §9/§6 are corrected where they were wrong.

### Phase 1 — The store

| | |
|---|---|
| 1.1 | EF model + migration for §2–§5 of the data model (workspace, kinds, item, criteria, links, docs, blocks). Trigger for `ancestors`; fractional `seq` helpers. |
| 1.2 | Evidence + journal: attempts, reports, verifications, artifacts, events, `pg_notify`. |
| 1.3 | Threads: assumptions, constraints, notes, claims, tokens. |
| 1.4 | Query services: frontier, rollup, subtree, open-threads, search. Hand-written SQL, `EXPLAIN`-checked against a seeded 5,000-item workspace. |

*Verify:* integration tests against a throwaway Postgres in compose. *Seam:* `ancestors` maintenance
vs `work_move` — the one place the tree can silently corrupt.

### Phase 2 — The MCP server, read side

| | |
|---|---|
| 2.1 | Host + transport + bearer auth + the three token profiles, with the scoped `tools/list`. |
| 2.2 | `work_brief`, `work_tree`, `work_get`, `work_next` + the compact renderer + `outputSchema`. |
| 2.3 | `work_search`, `work_threads`, `work_history`. |
| 2.4 | The `attention` block, computed once and attached to every result. |

*Gate:* register against Claude Code and drive a seeded workspace end to end, reading only through
tools. **Measure the token cost of `work_brief` on the imported `entity-store` run against the 6,011
lines it replaces** — that number is the project's headline claim and it should be recorded, not
assumed.

### Phase 3 — Write side and the guards

| | |
|---|---|
| 3.1 | `work_create` (batch, with the kind rules), `work_update`, `work_move`, `work_link` + cycle check. |
| 3.2 | `work_status` — the full transition guard, `force` + `forced_transition` events, deferral-as-a-move. |
| 3.3 | `work_report`, `work_verify`, `work_note`, `work_assume`, `work_finding`. |
| 3.4 | `work_patch` — block ops with `expected_revision` + block revision history. |
| 3.5 | Claims, leases, heartbeats, the serialization scope. |

*Gate — required.* Seams: the `done` guard reads criteria **and** verifications **and** findings
**and** children; `supersedes` mutates two rows; a claim's scope index must not deadlock against
`work_move`. Every guard needs a red-check: a test proving the refusal fires, written against the
*un-guarded* code first.

### Phase 4 — Prompts, resources, import

| | |
|---|---|
| 4.1 | `work_import` + `tools/WorkState.Import`: parse a `.claude/orchestration/<slug>/` directory into items, docs and blocks, preserving every original line verbatim. Run it over all 181 files; the failures are the specification for what the schema is missing. |
| 4.2 | Resources + templates + completions. |
| 4.3 | Prompts: `worker_brief`, `review_brief`, `gate_brief`, `plan_audit_brief`, `handoff`, `retro`, `decompose` — with note-consumption on render. |
| 4.4 | `work_audit` — the mechanical checks. |

### Phase 5 — Operator dashboard

App API (cookie auth, excluded from the service OpenAPI) + SSE from `LISTEN/NOTIFY` + the SolidJS
SPA. Design deferred, per the brief. Stack fixed in §1.

### Phase 6 — The skills

Rewrite `orchestrate`, `controller` and `nested-claude` against the store. This is the payoff and it
is a **separate undertaking**, gated on Phase 4 being real:

- `references/decompose.md` § *PLAN.md* and § *LEDGER.md* → deleted; the store owns both shapes.
- `references/briefs.md` templates → server prompts.
- `assets/LEDGER.template.md`, `assets/CONVENTIONS.template.md` → deleted.
- `SKILL.md` keeps the judgment: the role change, the cost table, the risk dial, red-checks,
  verification doctrine, the phase gate's question, escalation triggers, the critical-thinking
  ceiling. Rough estimate: **333 lines → ~180**, and `briefs.md`'s 311 lines mostly evaporate.
- `controller` §7 *"The record is a deliverable"* becomes *"the record is a query"*.
- **Both stores run in parallel for one real undertaking before the files are removed.** The corpus
  is unambiguous that the failure mode of a migration is two artifacts that disagree.

---

## 4. Decisions taken, with their reversal cost

| Decision | Cost if wrong |
|---|---|
| One recursive `item` table, kinds as data | **Low.** Adding a kind is a row. Splitting it later is a migration but the API does not change. |
| `ancestors uuid[]` + GIN, not `ltree`, not a closure table | **Low.** Both alternatives are a rewrite of one trigger and one index. |
| Fractional `numeric` `seq` | **Low.** Rebalance job; or switch to LexoRank strings. |
| Blocks with stable ids instead of markdown blobs | **Medium.** It is the whole patch story; changing it changes `work_patch` and every renderer. Committed deliberately — see doc 00 §4. |
| ~~Compact text in `content`, JSON in `structuredContent`~~ → **text only** | **Superseded by measurement, 2026-09-18.** `structuredContent` *replaces* the text block, so the original decision would have made the compact rendering invisible — doc 01 §2. Reversal cost of the *new* decision: **trivial**, a `format: "json"` argument if a programmatic MCP consumer ever appears. |
| Confirmation via server-minted handle, not MRTR elicitation | **Trivial.** Add MRTR alongside when client support is uniform. |
| Scoped tokens drive `tools/list` | **Settled: it works.** `[Authorize]` alone narrows the list; reader saw 2 tools, writer 3. No caching issue — lists are per-registration. |
| Specs stay in git; the store owns state | **Low**, and it is the corpus's own rule. |
| EF Core for writes, raw SQL for the recursive reads | **Low.** |

### Settled with the owner, 2026-09-18

1. **One global MCP deployment; a `project` item per repo.** Many orchestrators run in parallel, each
   owning its own **run** subtree inside a project. Refs are scoped to the nearest `project`/`run`
   (`scopes_refs`), so `2.3` stays short and cannot collide across parallel runs — see doc 02 §2.
   The write lock is keyed on `serialization_key` (the worktree), so separate worktrees proceed
   concurrently and a shared tree does not.
2. **Artifacts are pointers.** `artifact` rows carry paths, shas and URLs; bytes stay on disk.
3. **Single user.** No RLS, no `owner` column; `workspace` is a config singleton and the seam if that
   ever changes.

---

## 5. The risk I would flag before you commit to this

The reason those 55,523 lines exist is not only that files were the only store — it is that **prose
carries reasoning that rows cannot**. `Gaia/prod-log-triage/fixes/B-09/LEDGER.md` § *"Two judgements
inside it worth keeping"* is genuinely load-bearing and is genuinely a paragraph.

So the design must not try to normalise everything. The split is:

- **Rows** for anything with a lifecycle, an identity, or a closure obligation — which is where all
  the measured failures are.
- **Blocks** for reasoning, attached to the row it belongs to, patchable, revisioned, and
  **rendered into the brief of the item it was addressed to**.

A store that only accepts rows will be routed around within one run, and you will end up with both a
database and a 6,000-line ledger. The block layer is not a convenience — it is what stops that.
