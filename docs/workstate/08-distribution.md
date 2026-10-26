# 08 — Distribution, lifecycle, and the always-on frontend

Four questions from the owner, answered against what is verified rather than assumed.

---

## 1. Lifecycle — answer this first, the rest follows from it

**The transport decides who owns the process**, and we chose HTTP.

| | **stdio** | **Streamable HTTP** ← ours |
|---|---|---|
| Who starts it | Claude Code, per session | **you** |
| Who kills it | Claude Code, at session end | **you** |
| Concurrent agents | one process each | **one server, many clients** |
| Alive between sessions | ✗ | **✓** |
| Can host a web UI | ✗ — it dies with the session | **✓** |

So: **WorkState is not "always running" because it is a plugin. It is always running because you
run it.** Claude Code only ever *connects*; it never manages the process.

Two consequences that were not obvious when the transport was chosen:

- **The parallel-orchestrator design needs this.** Doc 02 §2 has many orchestrators writing at once
  against one store, arbitrated by `claim_one_writer_per_tree`. Under stdio each session would get
  its **own process**, all pointed at one Postgres — which would still work, but there would be no
  stable host for a UI and no single place to watch from.
- **An always-on frontend is only possible because of it.** Under stdio the dashboard would exist
  only while an agent happened to be working, and would die mid-page-load when the session ended.
  The transport choice made months of frontend design viable by accident. Worth noticing.

---

## 2. Can the marketplace distribute it? — Yes, but only the *pointer*

**Verified:** a plugin auto-discovers `.mcp.json` (AGENTS.md), and Claude Code stores an HTTP server
in exactly this shape — read back from the live config after `claude mcp add`:

```json
{ "workstate": { "type": "http",
                 "url": "http://localhost:5290/mcp",
                 "headers": { "Authorization": "Bearer …" } } }
```

**A plugin cannot ship a running server.** Three models, and only one survives contact with the
frontend requirement:

| | Model | Verdict |
|---|---|---|
| **(a)** | Plugin ships **`.mcp.json` pointing at `127.0.0.1:5290`**; the user runs the stack | ✅ **this one** |
| (b) | Plugin ships the .NET project; Claude spawns it per session over stdio | ✗ kills the frontend; and every session migrating a shared Postgres is a race nobody needs |
| (c) | stdio shim that proxies to the HTTP server, starting it if absent | ✗ over-engineered, and it hides a failure you want loud |

So `plugins/rlarch/.mcp.json` becomes:

```json
{ "mcpServers": {
    "workstate": { "type": "http", "url": "http://127.0.0.1:5290/mcp",
                   "headers": { "Authorization": "Bearer ${WORKSTATE_TOKEN}" } } } }
```

### `${VAR}` expansion — **measured 2026-09-21, it works**

Doc 08 shipped with this marked UNVERIFIED and a standing instruction to probe it. Probed, both
directions, on the path we would actually ship:

| Run | Config | `WSPROBE_TOKEN` | Result |
|---|---|---|---|
| 1 | `--mcp-config .mcp.json` | `dev-controller-token` | connected · **18 tools** |
| 2 | same | **unset** | `HTTP 401` · no tools |
| 3 | **`--plugin-dir ./plug`** (a plugin's own `.mcp.json`) | `dev-worker-token` | connected · **6 tools** |
| 4 | same | **unset** | `AUTH_HEADER_REJECTED`, `HTTP 401` · no tools |

Runs 3 and 4 are the ones that settle the packaging question: **a plugin-provided `.mcp.json`
expands `${VAR}` inside `headers`.** Runs 2 and 4 are the red-check — without the variable the header
goes out literally and the server rejects it, so run 1's success was the token and not anonymous
access.

Three things the probe established that the design did not know:

- **Only `${VAR}` and `${VAR:-default}` expand. Bare `$VAR` does not.** The `$VAR` form documented
  in Claude Code belongs to **HTTP hooks**, which are a different feature with their own
  `allowedEnvVars` gate. Doc 08 guessed the right form by luck; the file must keep the braces.
- **The variable's NAME is load-bearing.** The client carries a blank-list of credential-shaped
  names that are deliberately read as **empty** when expanded toward a remote server, with a warning
  rather than an error. It targets specific ecosystems — `GIT_CONFIG_*`, `CARGO_REGISTRIES_*_TOKEN`,
  `BUNDLE_*__*`, package-registry user/token names, `OTEL_*`, and Claude's own credential variables.
  `WSPROBE_TOKEN` is not on it and neither is `WORKSTATE_TOKEN`, but this is a **trap for anyone who
  later renames it to something more obviously secret**: the failure is a silent 401, not a message
  saying the name was refused. (Read from the client bundle, not measured — unlike the table above.)
- **Plugin tools are namespaced `mcp__plugin_<plugin>_<server>__<tool>`**, not `mcp__<server>__`.
  Any skill or profile that names a tool explicitly has to use that longer form.

**A bonus the probe was not looking for:** runs 1 and 3 differ only in the token, and returned **18
tools versus 6**. That is `AddAuthorizationFilters` and the three role profiles working end-to-end
through a real client — previously only asserted in unit tests. Doc 01 §6's claim that a tool an
agent may not call is simply *absent* from `tools/list` is now measured from the outside.

### The one thing still to settle: **never commit a real token**

`github.com/r-Larch/skills` is public. Today's `dev-*-token` values are localhost-only throwaways,
which is why they are committed — but a marketplace file is a different audience, and a token in it
is a token on the internet. The `${WORKSTATE_TOKEN}` indirection is what keeps the shipped file
clean, and it is now known to work.

### And the version-skew problem, which is new

The plugin and the server now **version separately**: the plugin updates through the marketplace, the
server when you rebuild it. A skill that assumes `work_status` exists breaks against an older server,
**silently**, because a missing tool is simply absent from `tools/list` (measured — doc 01 §6).

`McpServerOptions.ServerInfo.Version` is already set (`0.4.0` as of rlarch 2.2.0). **Bump it deliberately**, and have
the rewritten skills state the minimum they need, so the failure is a sentence rather than a mystery.

`0.5.0` (rlarch 2.3.0) carries one project per repository (repo binding), the project brief, archive,
the prompt catalogue and `work_import`.

---

## 3. Should the skills use it? — Yes, and as a **hard** dependency

> **SCOPE CORRECTED 2026-09-22 — it is two skills, not three.** This section and doc 03's Phase 6
> both say *“rewrite `orchestrate`, `controller` and `nested-claude`”*. Measured: `nested-claude`
> contains **zero** references to a ledger, `PLAN.md`, `CONVENTIONS.md` or the orchestration
> directory. Its nine sections are launching a session, addressing it, the READY/BLOCKED/DONE
> protocol, steering, watching, wrapping up, concurrency, restart recovery and writing the brief.
> **None of that is bookkeeping a store can hold** — it is the mechanics of running a process.
>
> It needs at most one sentence pointing at where binding rules now live. The rewrite is
> `orchestrate` and `controller`.

The tempting answer is dual-mode: use the store if present, fall back to markdown. **Don't.** That is
exactly the "two overlapping artifacts, you maintain both and trust neither" failure `orchestrate`
warns about — and the file path would rot, because nobody would exercise it.

**Recommended shape:**

- **Hard dependency, with a preflight.** The skill's first instruction is `work_brief`. If the server
  is unreachable, the skill says **the one command that starts it**. That is
  `nested-claude`'s *"every prohibition needs a route"* applied to a skill: a requirement without a
  route is a trap.
- **This is a MAJOR bump** per AGENTS.md — the behaviour of an existing skill changes for everyone.
- **What gets deleted, not adapted:** `references/decompose.md` §PLAN.md and §LEDGER.md,
  `assets/LEDGER.template.md`, `assets/CONVENTIONS.template.md` — the store owns those shapes now.
  Rough estimate from doc 03 §Phase 6: `orchestrate` 333 → ~180 lines.

  > **CORRECTED 2026-09-22.** This bullet used to add *“and most of `references/briefs.md` … its
  > 311 lines mostly evaporate”*, on the strength of doc 03 §4.3's plan to move brief templates to
  > **MCP prompts**. **Prompts were cancelled by measurement on 2026-09-18** — they are invisible
  > to the client (doc 01 §9, [`PROBE.md`](PROBE.md)) — so the destination that bullet assumed does not
  > exist. The stale plan survived here because the correction was made in doc 01 and doc 03 and
  > never chased into this file.
  >
  > And the deeper reason it was wrong: **a brief for a nested session is not bookkeeping.** Its
  > allow-list, its banned-shell-shapes table, its “established facts, do not verify them” block and
  > its named environment noise are the things that stop an agent parking — fourteen launches in
  > the WorkState run were lost to defects in exactly those sections, and **not one** would have
  > been prevented by anything the store holds. `work_brief` renders an *item's* state; it knows
  > nothing about which verbs a session may run. **`briefs.md` mostly stays.**
- **What stays, because the store cannot hold judgement:** the cost table, the risk dial, red-checks,
  verification doctrine, the phase gate's question, escalation triggers, the critical-thinking
  ceiling.

**Sequencing:** this is doc 03's Phase 6 and it was always gated on the server working. It now does,
measurably — so it is unblocked, and it is a separate undertaking, not a tail on this one.

---

## 4. The always-accessible frontend — one process, two audiences

The `nomos-service` blueprint already prescribes the shape, and the design has followed it since
doc 03: **the SPA is built into `wwwroot` and served by the same ASP.NET host.**

```
        ┌──────────────── WorkState.Api (one process, always on) ─────────────┐
agents →│  /mcp      Streamable HTTP · bearer token · three role profiles     │
human  →│  /         SolidJS SPA from wwwroot                                 │
        │  /api/*    App API · cookie session · excluded from the OpenAPI doc │
        │  /api/stream  SSE ← Postgres LISTEN/NOTIFY on event.seq             │
        └─────────────────────────────────────────────────────────────────────┘
                                    ↓
                        Postgres (the only state)
```

**Two auth schemes that never cross** is `nomos-service` non-negotiable #2, and it is the reason one
process can serve both safely: a bearer token must never satisfy the dashboard, and a cookie must
never reach `/mcp`.

**The read API is the one shared door (doc 10 rule 5).** `GET /api/v1/*` and `/api/stream` are
read-only, so they take either audience: the dashboard cookie, any agent token, or a **reader token**.
The pre-v1 cookie-only `/api/*` routes are removed; `/api/session`, `/api/agent-sessions` (doc 14
§2, bearer only) and `POST /api/projects/{id}/archive|unarchive` (doc 10, cookie only, with the
`X-WorkState-Write` header) are the only other `/api` routes.

| Token | Config / env | Reaches |
|---|---|---|
| worker · orchestrator · controller | `WorkState:Auth:{Worker,Orchestrator,Controller}Token` · `WORKSTATE_{WORKER,ORCHESTRATOR,CONTROLLER}_TOKEN` (required in compose) | `/mcp` by role, and `GET /api/v1/*` + `/api/stream`; orchestrator and controller also `/api/agent-sessions` |
| reader | `WorkState:Auth:ReaderToken` · `WORKSTATE_READER_TOKEN` (**optional**: compose passes `${WORKSTATE_READER_TOKEN:-}`, and empty means no reader token exists) | `GET /api/v1/*` and `/api/stream` only — `/mcp` refuses it (403), and so does `/api/session` |
| minted `wss_…` (per session) | none — `POST /api/agent-sessions` with an orchestrator or controller bearer returns it once; the store keeps only its SHA-256. Expires after `ttlHours` (default 72, max 720) **unused** — an idle window each use renews, no hard cap — revoked by `DELETE` | exactly what a static token of its granted role reaches (never above its minter's role), and its calls are that session's: `X-WorkState-Agent` is ignored |

Foreman authenticates with the reader token: it watches every project and can write nothing.

### How to make it always-on

**`mcp/compose.yml` already has `restart: unless-stopped` on both services.** So:

```
docker compose -f mcp/compose.yml up -d
```

…and the UI is at `http://127.0.0.1:5290` permanently, surviving reboots wherever Docker Desktop
starts at logon. Agents reach `/mcp` on the same host and port. **Nothing needs to know an agent is
working** — the SSE stream is fed by `LISTEN/NOTIFY` on the event journal, so the board updates
because the *database* changed, not because a session is open.

That is the payoff of the event table being append-only with a monotonic cursor: the human's view
and the agent's view are the same data, and neither depends on the other being present.

**Without Docker:** publish the API and run it as a Windows service (`sc.exe create`) or a scheduled
task at logon. Same result, more moving parts.

### Two things the always-on model brings that are worth deciding now

- **It becomes a single point of failure and a single backup target.** Every project's plan, ledger
  and evidence lives in one Postgres volume. **Back that volume up**, or the thing that replaced 181
  markdown files in git becomes the one artifact that is not in git.
- **Bind to localhost.** `compose.dev.yml` already publishes `127.0.0.1:55433`. Keep the API the same
  until there is a reason not to, because the bearer tokens are currently throwaways and the
  dashboard's auth is a cookie over plain HTTP.

---

## Recommended order

1. ~~**Probe `${VAR}` expansion** in `.mcp.json`.~~ **Done 2026-09-21 — it expands** (§2). Four runs,
   both directions, on the plugin path.
2. **Ship `plugins/rlarch/.mcp.json`** + a short README section: *start the stack, set the token*.
   Minor bump — additive. **Staged, not shipped:** the exact file is committed at
   **`mcp/plugin.mcp.json`**, verified by the §2 probe. Shipping it is `cp mcp/plugin.mcp.json
   plugins/rlarch/.mcp.json` plus the version bump, and that is deliberately one manual step — see
   below.
3. **Build the dashboard** (doc 03 Phase 5; stack fixed, design deferred). The host needs
   `UseStaticFiles` + `MapFallbackToFile` — **not present yet**, confirmed by grep.
4. **Then rewrite the three skills** against the store. Major bump. Separate undertaking.

Order matters: the skills should be rewritten **after** the dashboard exists, because the first
thing a human will want when a skill starts writing to a store instead of a file is somewhere to
watch it happen.

### Why the file is staged rather than committed at its live path

It is written and proved, so the temptation is to drop it straight into `plugins/rlarch/`. Two
reasons not to, and the second is the real one:

- **It is not free to install.** The moment it ships, every session in every repo with the rlarch
  plugin tries to reach `127.0.0.1:5290`. If the stack is not up or `WORKSTATE_TOKEN` is unset, the
  result is a failed MCP connection notice — in every project, whether or not that project has
  anything to do with WorkState. That is a cost the owner should choose, not inherit.
- **An unbumped file in `plugins/rlarch/` is a landmine.** It reaches nobody today, which reads as
  safe — but the next version bump for an unrelated reason ships it silently, to everyone, with
  nobody having decided to. Distribution here is gated on one field, so anything sitting inside the
  plugin directory is armed and waiting for a trigger someone else will pull for other reasons.
  Keeping it at `mcp/plugin.mcp.json` means the bump cannot ship it by accident; only the `cp` can.

So this is one `cp` away from shipped, and the `cp` is the decision.
