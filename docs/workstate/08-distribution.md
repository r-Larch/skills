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
| **(a)** | Plugin ships **`.mcp.json` pointing at `localhost:5290`**; the user runs the stack | ✅ **this one** |
| (b) | Plugin ships the .NET project; Claude spawns it per session over stdio | ✗ kills the frontend; and every session migrating a shared Postgres is a race nobody needs |
| (c) | stdio shim that proxies to the HTTP server, starting it if absent | ✗ over-engineered, and it hides a failure you want loud |

So `plugins/rlarch/.mcp.json` becomes:

```json
{ "mcpServers": {
    "workstate": { "type": "http", "url": "http://localhost:5290/mcp",
                   "headers": { "Authorization": "Bearer ${WORKSTATE_TOKEN}" } } } }
```

### ⚠ Two things to settle before shipping that file

- **`${WORKSTATE_TOKEN}` expansion is UNVERIFIED.** I believe Claude Code expands `${VAR}` in MCP
  config; I have not proved it, and `claude mcp add --help` does not mention it. **Probe it before
  committing that file** — the same rule this project has applied throughout. If it does not expand,
  the fallback is a documented one-line `claude mcp add` in the skill, which is worse ergonomics and
  perfectly safe.
- **Never commit a real token.** `github.com/r-Larch/skills` is public. Today's `dev-*-token` values
  are localhost-only throwaways, which is why they are committed — but a marketplace file is a
  different audience, and a token in it is a token on the internet.

### And the version-skew problem, which is new

The plugin and the server now **version separately**: the plugin updates through the marketplace, the
server when you rebuild it. A skill that assumes `work_status` exists breaks against an older server,
**silently**, because a missing tool is simply absent from `tools/list` (measured — doc 01 §6).

`McpServerOptions.ServerInfo.Version` is already set (`0.3.0`). **Bump it deliberately**, and have
the rewritten skills state the minimum they need, so the failure is a sentence rather than a mystery.

---

## 3. Should the skills use it? — Yes, and as a **hard** dependency

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
  `assets/LEDGER.template.md`, `assets/CONVENTIONS.template.md`, and most of `references/briefs.md`
  — the store owns those shapes now. Rough estimate from doc 03 §Phase 6: `orchestrate` 333 → ~180
  lines; `briefs.md`'s 311 mostly evaporate.
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

### How to make it always-on

**`mcp/compose.yml` already has `restart: unless-stopped` on both services.** So:

```
docker compose -f mcp/compose.yml up -d
```

…and the UI is at `http://localhost:5290` permanently, surviving reboots wherever Docker Desktop
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

1. **Probe `${VAR}` expansion** in `.mcp.json`. One test, settles the packaging question.
2. **Ship `plugins/rlarch/.mcp.json`** + a short README section: *start the stack, set the token*.
   Minor bump — additive.
3. **Build the dashboard** (doc 03 Phase 5; stack fixed, design deferred). The host needs
   `UseStaticFiles` + `MapFallbackToFile` — **not present yet**, confirmed by grep.
4. **Then rewrite the three skills** against the store. Major bump. Separate undertaking.

Order matters: the skills should be rewritten **after** the dashboard exists, because the first
thing a human will want when a skill starts writing to a store instead of a file is somewhere to
watch it happen.
