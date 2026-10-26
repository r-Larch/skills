# 12 — Install, harness and identity: WorkState from a fresh PC

Owner: skills-5d. Written 2026-10-23 for René, who asked:

> find a way where we can set name and eventually the role with something the MCP spec supports …
> explain the WorkState MCP surface, the claude code harness MCP support (what does it support? does
> it keep MCPs stateless? does it keep a session between calls?), the procedure to start a
> nested-claude, the setup how the WorkState MCP is meant to be installed, where lives its
> configuration file? where should the binaries live? — from the perspective of a fresh PC.

Every statement is tagged with how it is known:

- **[measured]** observed on this machine (Claude Code **2.1.309**, Windows 11, WorkState @ `7cd34f7`)
- **[code]** read in this repo
- **[doc]** read in Claude Code docs (`code.claude.com/docs/en/…`) or the MCP specification
  (`modelcontextprotocol.io`), with the page named
- **[not documented]** looked for, not found — treat as unknown, not as "no"

---

## 0. The short answers

| Question | Answer |
|---|---|
| Does the harness keep an MCP session between calls? | **No — not with WorkState.** Claude Code 2.1.309 opens with the **2026-07-28** revision (`server/discover`), which removed `Mcp-Session-Id` and `initialize` altogether **[measured]** **[doc: SEP-2567, SEP-2575]**. WorkState's SDK (C# 2.2.0) is stateless by default **[code]**. Every request stands alone. |
| Who carries the token? | **The harness, never the model.** Claude Code reads the server entry from config at session start and attaches its `headers` (or the output of a `headersHelper`) to every HTTP request **[doc: mcp.md "headers and headersHelper"]**. The model only calls `work_*` tools. |
| Then why did agents touch tokens this week? | Because **our setup procedure makes them**: `workstate-setup` has the model *write* `mcp/.env` with generated tokens, *read* a token back out of it, paste it into `claude mcp add --header "Authorization: Bearer …"`, and `curl` with it **[code: workstate-setup §3, §5, §6]**. The harness then stores it in plaintext in `~/.claude.json` **[measured: the user-scope entry is `{type, url, headers:{Authorization}}`]**. Fixing *that* is part of the proposal below. |
| What can identify a session per request, spec-compliantly? | (1) **the credential** (the token's principal) — the spec's own replacement for session ids; (2) **headers the harness attaches**; (3) `clientInfo` in `_meta` — but it is the same `claude-code 2.1.309` for every session and the spec says not to base decisions on it **[doc + measured]**. |
| Can a session's **name** reach the server without the model? | **Yes — via `headersHelper`.** Measured today: a `claude --bg` session launched with `--mcp-config` whose server entry has `headersHelper: "<command with the name as an argument>"` sent that header on every request **[measured]**. Environment variables do **not** work for `--bg` sessions (launcher env and the worktree's `settings.local.json` `env` both arrived as unset) **[measured]**. |
| Can the **role** too? | **Yes, the same way** — the helper prints `Authorization: Bearer <the token for that role>`, reading the token from where the server's tokens already live (`mcp/.env`). The model never sees it; the helper runs in the harness, not in a tool call. This is "role in the credential", which is what the spec recommends **[doc: authorization, SEP-2567]**. |
| Recommendation | **A WorkState credential helper** (§7.3): one small script shipped in the plugin, used both by the normal registration and by every nested-agent launch. No token in `~/.claude.json`, none in any per-launch file, none in the model's context; name and role per session; nothing new on the server. |

---

## 1. The moving parts, and where each one lives

| Part | What it is | Where it lives on a PC | Who puts it there |
|---|---|---|---|
| **Marketplace** `rlarch` | a git clone of `github.com/r-Larch/skills` | `~/.claude/plugins/marketplaces/rlarch/` **[measured]** | `/plugin marketplace add r-Larch/skills` (auto-updates: `autoUpdate: true` in `~/.claude/plugins/known_marketplaces.json`) |
| **Plugin** `rlarch` | the skills only: `plugins/rlarch/` | `~/.claude/plugins/cache/rlarch/rlarch/<version>/` **[measured: `…/1.5.0/skills/controller`]** | `/plugin install rlarch@rlarch`; re-copied only when `plugin.json` `version` changes |
| **WorkState source** | `.NET 10` API + MCP server + SolidJS dashboard, `mcp/` in the same repo | a **separate clone you choose**, e.g. `P:\Projects\LarchSys\Skills` | `workstate-setup` §2 asks where, then `git clone` |
| **WorkState binaries** | a Docker image `workstate-api` built **from that clone**, plus `postgres:17` | Docker's image store | `docker compose -f mcp/compose.yml up -d` (multi-stage build: `node:24-slim` builds the SPA, `dotnet/sdk:10.0` publishes, `aspnet:10.0` runs) **[code: mcp/Dockerfile]** |
| **WorkState data** | the Postgres database: every plan, claim, verification, journal | Docker volume `workstate-db-data` | created on first `up`; schema applied by the API at startup **[code: SchemaOwner]** |
| **Server configuration** | tokens, dashboard login, Postgres password, port, bind | `<clone>/mcp/.env` (gitignored; template `mcp/.env.example`) | `workstate-setup` §3 |
| **Client registration** | the `workstate` MCP server entry | `~/.claude.json` → `mcpServers.workstate` (user scope) or `projects["<path>"].mcpServers` (local scope) **[measured]** | `claude mcp add` (`workstate-setup` §5) |
| **Running service** | `http://127.0.0.1:5290` — `/mcp`, `/api/v1/*`, dashboard, `/health` | containers `workstate-api-1`, `workstate-db-1`, `restart: unless-stopped` | Docker |

Nothing of WorkState runs inside Claude Code. It is a service **you** run; Claude Code only connects
(doc 08 §1). The host needs **no .NET SDK and no Node** for WorkState — both live in build stages of
the image. It needs **git** and **Docker**.

Two facts worth knowing:

- **The plugin ships no `.mcp.json`** on purpose (README "WorkState"): a shipped server entry is
  attempted by every session in every project, so every user who installed `rlarch` for
  `dotnet-reflect` would get a failed connection forever.
- **The marketplace clone already contains `mcp/` once 2.x is pushed.** Today `origin/master` is
  1.5.0, so `~/.claude/plugins/marketplaces/rlarch/` has no `mcp/` **[measured]**. After the release
  it will. Building from there is tempting and wrong: Claude Code owns that directory and
  auto-updates it with `git pull`, so a working server could change under you. Keep the separate
  clone (§9 open question 3 has the alternative: a published image).

---

## 2. A fresh PC: `/plugin marketplace add r-Larch/skills`, then `/plugin install rlarch@rlarch`

**Step 1 — `/plugin marketplace add r-Larch/skills`.** Claude Code clones the repo's default branch
into `~/.claude/plugins/marketplaces/rlarch/` and reads `.claude-plugin/marketplace.json`, which lists
one plugin, `rlarch`, with `source: ./plugins/rlarch` **[code]**. Nothing is installed yet.

**Step 2 — `/plugin install rlarch@rlarch`.** Claude Code copies `plugins/rlarch/` into
`~/.claude/plugins/cache/rlarch/rlarch/<version>/` (version from `plugin.json`, today `2.1.0` on
`feat/workstate`, `1.5.0` published) and records it in `installed_plugins.json`. The plugin contains
eight skills and their assets — no hooks, no MCP servers, no agents **[code]**. In the **next**
session the skills are listed as `/rlarch:<name>`.

What is **not** there after step 2:

- no WorkState server, no Docker containers, no `mcp/.env`
- no `workstate` MCP registration, so no `work_*` tools
- `/rlarch:dotnet-reflect`, `dotnet-source`, `ts-source`, `code-comments`, `nested-claude` work
  immediately (given their prerequisites — README "Prerequisites and platforms")
- `/rlarch:orchestrate` and `/rlarch:controller` **cannot run**: they require WorkState and have no
  markdown fallback since 2.0.0

---

## 3. The first `/rlarch:controller` after a fresh install

### 3.1 What the controller does

The controller skill's §0 preflight **[code: plugins/rlarch/skills/controller/SKILL.md]**:

1. "Call `work_brief`." → the tool does not exist in this session.
2. It says in one line: *"controller needs the WorkState MCP server and this session cannot see it —
   run `/rlarch:workstate-setup`."* and **stops**. No queue, no agents.

### 3.2 What `/rlarch:workstate-setup` does today

**[code: plugins/rlarch/skills/workstate-setup/SKILL.md]**

| § | Step | Who handles a secret |
|---|---|---|
| 1 | Classifies the machine into one of four states with `claude mcp get workstate` and `curl …/health`. Fresh PC: **nothing present**. | — |
| 2 | Looks for `mcp/compose.yml`; **asks** where to clone; `git clone https://github.com/r-Larch/skills <dir>`. | — |
| 3 | **The model writes `mcp/.env`** with `Write`, inventing random values for three tokens, the dashboard password and the Postgres password; then `git check-ignore -v mcp/.env`. | **model** |
| 4 | `docker compose -f mcp/compose.yml up -d` — first build takes minutes; waits for `(healthy)`. | — |
| 5 | **The model reads a token** (orchestrator or controller) out of `mcp/.env` and runs `claude mcp add --scope user --transport http workstate http://…:5290/mcp --header "Authorization: Bearer <token>"`. The harness stores it in `~/.claude.json` in plaintext. | **model**, then harness file |
| 6 | **The model `curl`s `tools/list` with the token** and counts tools (19 = orchestrator/controller, 6 = worker). | **model** |
| 7 | Tells the user to start a new session — MCP servers resolve at session start. | — |

### 3.3 Then, in the new session

`/rlarch:controller` again → `work_brief` answers. On an empty server it returns onboarding
(how to create a project). The controller creates `project → run → tasks` (`work_create`), records
binding rules (`work_constrain`), orders the queue (`work_link … blocks`), gets the human's one
approval, and starts launching nested agents (§6).

### 3.4 What is wrong with that path

1. **Secrets pass through the model three times** (§3, §5, §6). The auto-mode classifier is right to
   distrust this; it refused the analogous step this week (`[Credential Leakage]`, writing a token
   into a per-launch config).
2. **One token for every session.** The user-scope registration carries one role's token, and every
   session on the machine — including every nested agent — inherits it **[measured]**. A nested
   worker therefore writes with the controller's authority, and the done-guard (a worker's
   verification cannot close a task) is silently off for exactly the agents it exists for.
3. **No session names** unless each registration carries `X-WorkState-Agent` (doc 11), which a
   user-scope registration cannot do per session.
4. The registration URL is `http://localhost:5290/mcp` **[measured]** while the server binds
   `127.0.0.1` only; a client that tries `::1` first fails (§9 open question 4).

---

## 4. The WorkState MCP surface

**[code unless marked]** — full contract in docs 01, 05–07, 10, 11.

### 4.1 Transport and protocol

- Streamable HTTP at `POST /mcp`, ASP.NET Core, `ModelContextProtocol.AspNetCore` **2.2.0**.
- **Stateless** (SDK default since the 2026-07-28 revision): no `Mcp-Session-Id`, no per-connection
  state; `ConfigureSessionOptions` runs per request **[doc: SDK XML docs, via reflection]**. The SDK
  still answers older `initialize` clients, statelessly.
- Server instructions (`WorkStateServerInstructions.Text`, doc 05) are delivered with discovery.

### 4.2 Tools — 19, gated by role

`AddAuthorizationFilters` hides what a token may not call: a tool is **absent** from `tools/list`,
not refused (doc 01 §6, measured in doc 08).

| Role needed | Tools |
|---|---|
| **worker** (6) | `work_brief`, `work_get`, `work_search`, `work_report`, `work_verify`, `work_note` |
| **orchestrator** (+13) | `work_next`, `work_tree`, `work_threads`, `work_audit`, `work_history`, `work_status`, `work_create`, `work_update`, `work_move`, `work_link`, `work_assume`, `work_constrain`, `work_finding` |
| **controller** | same tools as orchestrator (the role is recorded; tokens nest: controller ⊇ orchestrator ⊇ worker) |

Every call passes through two request filters before the tool runs:

1. `ToolArguments` — undeclared or wrong-shaped arguments are refused with the nearest real
   parameter and a call that binds (2026-10-23, `67171db`).
2. `CallerSession` — reads the `X-WorkState-*` headers and attaches a session (doc 11, `04015e2`).

### 4.3 Resources

`work://workspace`, `work://workspace/brief`, `work://workspace/conventions`,
`work://workspace/handoff`, and per item `work://item/{ref}` and `work://item/{ref}/brief`.

### 4.4 Authentication and identity — what the server believes

| Source | What it establishes | Trust |
|---|---|---|
| `Authorization: Bearer <token>` (3 static tokens + optional reader token, from `mcp/.env`) | the **role** → tool visibility, `run_by` on evidence, who may close | the only authority |
| `X-WorkState-Agent` (+ `-Session`, `-Launched-By`, `-Origin`, `-Cwd`, `-Foreman-Role`) | the **session** row: name, labels, lease renewal, one-session-per-tree | a label; never grants anything |
| dashboard cookie | the operator, for `/api/*` and the UI | — |
| `_meta.clientInfo` | not used (it would say `claude-code` for every session) | spec: self-reported |

### 4.5 Next to MCP

`GET /api/v1/*` (doc 10, read-only JSON for the dashboard and Foreman), `GET /api/stream` (SSE),
the dashboard at `/`, `/health`. Same port, same container.

---

## 5. What the Claude Code harness supports for MCP

| Topic | What is true | Source |
|---|---|---|
| Transports | `stdio`, `http` (alias `streamable-http`), `sse` (deprecated), `ws` | [doc: mcp.md "Transports"] |
| Protocol | Tries **2026-07-28** first (`POST server/discover`, headers `MCP-Protocol-Version: 2026-07-28`, `Mcp-Method`), falls back to `initialize` with `2025-11-25` when the server refuses | [measured] |
| Per-request identity it sends | `User-Agent: claude-code/2.1.309 (cli)`; `_meta["io.modelcontextprotocol/clientInfo"] = {name: "claude-code", version: "2.1.309", …}` — **identical for every session** | [measured] |
| Session id between calls | none under 2026-07-28 (removed from the spec); whether it resends one to an old stateful server is not documented | [doc: SEP-2567] [not documented] |
| When it connects | at session start (unless a cached tool list defers it to first use); retries remote first connect up to 3×, mid-session drops up to 5× with backoff; `/mcp reconnect` | [doc: mcp.md "Reconnect behavior"] |
| New servers mid-session | no — servers resolve at session start; a registration made now appears in the **next** session | [measured; workstate-setup §7] |
| Scopes | local (`~/.claude.json` → `projects[path]`), project (`.mcp.json`, needs approval), user (`~/.claude.json`), plugin (`.mcp.json` in the plugin), managed | [doc: mcp.md "Scopes and storage"] |
| Same name in two scopes | local > project > user > plugin > claude.ai connectors; **the whole entry wins, fields are not merged** | [doc: mcp.md "precedence"] |
| `--mcp-config` vs a same-named user server | the `--mcp-config` entry **replaces** it (the nested agent had *only* the per-launch `workstate`, and lost the tools when its token failed) | [measured] |
| `--mcp-config` with `claude --bg` | honoured | [measured] |
| Static `headers` | yes; `${VAR}` / `${VAR:-default}` expansion in `url`/`headers` from the **harness process environment**; credential-looking names read as empty toward remote servers | [doc: mcp.md ".mcp.json format and expansion"; doc 08 measured] |
| Env in a `--bg` session | **not inherited** from the launching shell (`${WSPROBE_LAUNCH}` → unset), and the worktree's `.claude/settings.local.json` `env` does **not** feed header expansion either (`${WSPROBE_NAME}` → unset) | [measured] |
| `headersHelper` | a shell command, run fresh at session start, on reconnect and after a 401/403; prints a JSON object of headers; 10 s timeout; overrides same-named static headers; receives `CLAUDE_CODE_MCP_SERVER_NAME`, `CLAUDE_CODE_MCP_SERVER_URL` (and `CLAUDE_PLUGIN_ROOT` for plugin servers) — **no session id or name** | [doc: mcp.md "headers and headersHelper"] |
| `headersHelper` in a `--bg` session | **works** — the header arrived on every request, value taken from the command's argument | [measured] |
| OAuth | RFC 9728 → RFC 8414 discovery, dynamic registration or `oauth.clientId`, tokens in the keychain/credentials file, refreshed automatically; **per server, not per session** | [doc: mcp.md "OAuth"] |
| Plugin `userConfig` | `sensitive: true` values go to secure storage; `${user_config.KEY}` substitutes into the plugin's MCP config — **but `headersHelper` rejects `${user_config.*}`** | [doc: plugins/manifest-reference.md] |
| Roots | answers `roots/list` with the launch directory (+ added dirs) — needs a server→client request, which a stateless server cannot easily make | [doc: mcp.md "Roots"] |
| Session name / id as env var | `--name` shows in agent view and `claude agents --json` only; no `CLAUDE_SESSION_ID` env var | [not documented] |

The one-line summary: **the harness owns the connection and its headers; it gives each session the
same client identity; the only per-session lever it hands us is which config the session starts
with — and inside that config, a `headersHelper` command line.**

---

## 6. Starting a nested Claude — what actually happens

**[code: plugins/rlarch/skills/nested-claude/]** — mechanics in that skill, discipline in
`controller`.

1. The controller makes a **worktree + branch** per unit (`git worktree add -b ws/<unit> …`),
   claims the unit in WorkState with `tree:` = that worktree.
2. It writes a **brief file** and runs the launcher: `Start-NestedAgent.ps1` (Windows) or
   `start-nested-agent.sh` (macOS/Linux), which:
   - refuses a `-RepoRoot` that is not a working tree and a `-Controller` without its `[ref]`;
   - prepends the READY / BLOCKED / DONE protocol header and writes
     `<worktree>/.claude/briefs/brief-<name>.txt` (gitignored);
   - runs `claude --bg --name <name> --allowedTools … --add-dir … --permission-mode auto
     "Read the file <brief> and follow it exactly."` from inside the worktree — the variadic flags are
     ordered so each is closed by the next and the prompt survives;
   - returns `Name` (the `SendMessage` address) and `Id` (for `claude logs|attach|stop|rm`).
3. The background session starts under Claude Code's supervisor, **in the worktree's directory**.
   Its MCP servers come from its own config resolution: it **inherits the user-scope `workstate`
   registration** — same token, no name **[measured]**.
4. Protocol: `READY <task>` → work → `BLOCKED <q>` (controller answers) → `DONE <report>`. The
   controller arms a watchdog (`watch-nested-agent.py`) via `Monitor`, verifies independently,
   merges `--no-ff`, removes the worktree, stops the agent.

So today a nested agent is **anonymous and holds the controller role** — the gap §7 closes.

---

## 7. Identity: name and role, with what the spec and the harness support

### 7.1 What the MCP spec offers (2026-07-28)

**[doc: modelcontextprotocol.io — base protocol "Statelessness", "_meta"; authorization; SEP-2567]**

- Sessions are gone. Servers "MUST NOT rely on prior requests" for identity.
- `_meta` carries `protocolVersion`, `clientCapabilities` (required) and `clientInfo` (optional,
  self-reported, *"SHOULD NOT rely on them for security decisions"*). There is **no** reserved key
  for agent, user or session identity; custom vendor keys are legal, but nothing makes Claude Code
  send them.
- SEP-2567's stated replacement for session-keyed state: **the authenticated principal**, or an
  explicit state handle passed as a tool argument.
- Authorization: the server is an OAuth 2.1 resource server; **scopes** are the spec's model for
  "different principals see different tools". WorkState's one-token-per-role is the degenerate form
  of that (one scope per token) — spec-legal, since custom auth is allowed.

### 7.2 Options, judged against both constraints

| | Mechanism | Name | Role | Model touches a secret? | Works with `claude --bg`? |
|---|---|---|---|---|---|
| A | user-scope static header (today) | ✗ (one per machine) | one per machine | **yes, at setup** | inherits it |
| B | per-launch `--mcp-config` with a **static** token header | ✓ | ✓ | **yes** — the launcher/model writes the token into a file (classifier refused) | ✓ [measured] |
| C | per-launch `--mcp-config` with `${VAR}` | ✓ | ✓ | no | **✗ env not inherited** [measured] |
| D | worktree `.claude/settings.local.json` `env` + `${VAR}` | ✓ | ✓ | no | **✗ not expanded** [measured] |
| E | tool argument / state handle (`work_brief(agent:)`, SEP-2567 handle) | label only, model-asserted, lost on compaction | ✗ (never from the model) | no | ✓ |
| F | OAuth with WorkState as its own authorization server | per server, not per session | ✓ via scopes | no | per registration |
| **G** | **per-launch `--mcp-config` with a `headersHelper`** | **✓** | **✓** | **no** | **✓ [measured]** |

### 7.3 Recommendation — a WorkState credential helper (option G)

One small script in the plugin, `plugins/rlarch/skills/workstate-setup/assets/workstate-headers`
(`.sh` + `.ps1`, like every other launcher pair), invoked **only by the harness**:

```
workstate-headers --env <path to mcp/.env> --role worker|orchestrator|controller [--name <session>] [--launched-by <name>] [--cwd <path>]
```

It reads `WORKSTATE_<ROLE>_TOKEN` from the env file and prints one JSON object:

```json
{"Authorization":"Bearer <token>","X-WorkState-Agent":"aw-m1","X-WorkState-Launched-By":"skills-5d","X-WorkState-Cwd":"P:/…/m1"}
```

Two places use it:

1. **The normal registration** (`workstate-setup` §5) becomes, with no token anywhere in it:
   ```
   claude mcp add-json --scope user workstate '{"type":"http","url":"http://127.0.0.1:5290/mcp","headersHelper":"bash <plugin>/…/workstate-headers.sh --env <clone>/mcp/.env --role orchestrator"}'
   ```
   `~/.claude.json` then holds a *command*, not a secret. A human who wants a named session adds
   `--name <name>` in a local-scope registration for that project.
2. **The nested-agent launcher** writes a per-launch config with no secret in it:
   ```json
   {"mcpServers":{"workstate":{"type":"http","url":"http://127.0.0.1:5290/mcp",
     "headersHelper":"bash <plugin>/…/workstate-headers.sh --env <clone>/mcp/.env --role worker --name aw-m1 --launched-by skills-5d --cwd <worktree>"}}}
   ```
   and passes `--mcp-config <file>`. Measured: the entry replaces the user one, and the helper's
   headers arrive in a `--bg` session.

What this buys:

- **No secret in the model's context, in a tool call, in a per-launch file or in `~/.claude.json`.**
  The token stays in the one file the server already reads (`mcp/.env`). The helper runs inside the
  harness at connect time; its stdout goes into HTTP headers, not into the conversation.
- **Role per session, from the credential** — the spec's model. A nested agent launched as a worker
  holds the worker token: its `work_verify` is recorded as a worker's and can never close its own
  task. Nothing new on the server.
- **Name per session, durable across resume and compaction** — the helper re-runs on every
  (re)connect with the same arguments.
- Works for Foreman unchanged: Foreman can keep writing its own per-session configs (it may use the
  helper too).

What it does **not** buy, stated plainly:

- **Not a security boundary against a hostile agent.** Any session with shell access can read
  `mcp/.env`. It keeps an honest agent honest by construction, which is the threat model WorkState
  has (doc 07). The classifier's job — stopping the model from *handling* tokens — is satisfied.
- The helper path is baked into config. A plugin update changes the cache directory
  (`…/rlarch/<version>/`), so the registration must point at a **stable** copy: `workstate-setup`
  copies the helper next to `mcp/.env` in the clone (`<clone>/mcp/bin/workstate-headers.*`) and
  registers that path. (Measure first: whether `${CLAUDE_PLUGIN_ROOT}` is available in a *user-scope*
  `headersHelper` — documented only for plugin servers.)
- Windows: the helper is invoked through a shell; the `.sh` form needs Git Bash on PATH (Claude
  Code's own Bash tool already requires it). A `.ps1` twin covers PowerShell-only machines.

### 7.4 And the remaining secret-handling in setup

`workstate-setup` §3 has the model invent the tokens. Replace it with a script the model runs but
never reads the output of: `mcp/bin/init-env` writes `mcp/.env` with fresh random values and prints
only *which* keys it wrote. §6's `curl` proof becomes `work_brief` in the next session, or the helper
itself (`workstate-headers --check` prints the tool count, not the token). The dashboard password is
for the human: setup tells them where to read it (`mcp/.env`), it does not print it.

### 7.5 Later, if WorkState ever leaves localhost

Option F — WorkState as an OAuth 2.1 authorization server with **scopes = roles**
(`scopes_supported` in RFC 9728 metadata, 403 `insufficient_scope` for step-up). Claude Code already
does discovery, registration and keychain storage. It still gives one identity per registration, so
names would keep coming from the helper. Not worth building while the server is loopback-only.

---

## 8. What changes, if this is accepted

| Where | Change | Size |
|---|---|---|
| `plugins/rlarch/skills/workstate-setup/assets/` | `workstate-headers.sh` / `.ps1` (reads env file, prints headers JSON; `--check`); `init-env.sh` / `.ps1` | small |
| `workstate-setup/SKILL.md` | §3 runs `init-env`; §5 registers with `headersHelper` via `claude mcp add-json`, `127.0.0.1`; §6 checks via the helper, no token on a command line; a migration note for existing literal-token registrations | medium |
| `nested-claude` launchers (`.ps1` + `.sh`) | per-launch config with the helper, `--role` default `worker`; nothing secret in the file (rework of the deferred `ws/m6-launcher` branch) | medium |
| `controller` SKILL.md | one sentence: nested agents now write as workers under their own name | tiny |
| WorkState server | **none** — `X-WorkState-*` headers and per-role tokens already exist | — |
| Measurements before merging | (1) `${CLAUDE_PLUGIN_ROOT}` in a user-scope `headersHelper`; (2) `claude mcp add-json` with `headersHelper` round-trips into `~/.claude.json`; (3) a worker-role nested agent's `work_verify` is recorded as `worker` and cannot close its unit; (4) Git Bash absent → `.ps1` helper path | — |

---

## 9. Open questions for René

1. **Accept option G** (credential helper) as the identity design for names *and* roles? It
   reopens the deferred `launcher-headers` unit with no token in any file the model writes.
2. **Setup without the model handling secrets** (§7.4) — in this release, or after?
3. **Where should the WorkState binaries come from?** Today every PC builds the image from a clone
   (minutes, needs the source). Alternative: publish `ghcr.io/r-larch/workstate-api:<version>` and
   ship a `compose.yml` that pulls it — then a PC needs only Docker, `mcp/.env` and the compose file,
   and the clone becomes optional. Costs a CI pipeline and image versioning.
4. **`localhost` vs `127.0.0.1`.** The server binds IPv4 loopback only; registrations still say
   `localhost`. Either register `127.0.0.1` everywhere (planned for the release docs) or publish on
   `[::1]` as well.
5. **One server per machine, or per project?** Today one server holds every project (workspaces).
   Nothing in this design changes that; say so if you want otherwise.
