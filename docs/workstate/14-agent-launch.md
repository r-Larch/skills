# 14 — Launching agents through a tool, with per-session credentials

Owner: skills-5d. Decided by René 2026-10-23: *"why not providing a MCP tool to launch a nested
claude? … it runs in code and is authorized by the controller token and can generate and emit a token
for every role"* — **before the release**, and *"make sure the caller can set the permission mode and
other settings because automode sometimes works and sometimes a session needs acceptEdits"*.

Supersedes the credential-helper proposal of doc 12 §7.3 (option G), which only hid a static token.

---

## 1. The shape

```
controller model ──▶ agent_launch(name, brief, repo_root, controller, permission_mode, …, workstate:{role, env_file})
                     │  plugin stdio MCP server "agents" (Node, no dependencies) — a host process
                     │  started by Claude Code for the session; the model never sees what it handles
                     ├─ reads WORKSTATE_CONTROLLER_TOKEN from env_file (mcp/.env)        [code]
                     ├─ POST /api/agent-sessions {name, role, launchedBy, cwd} → per-session token
                     ├─ writes <repo_root>/.claude/briefs/mcp-<name>.json  (that token; owner-only)
                     ├─ writes <repo_root>/.claude/briefs/brief-<name>.txt (protocol header + brief)
                     ├─ runs  claude --bg --name <name> … --mcp-config <file> --permission-mode <mode> "<prompt>"
                     ├─ reads the session uuid from `claude agents --json`, PATCHes it onto the session
                     └─ returns {name, id, sessionId, brief, workstate:{session, role, expiresAt}}   ← no secret

agent_stop(id) ──▶ claude stop [rm] · DELETE /api/agent-sessions/{id} (revoke) · delete mcp-<name>.json
```

Why a **plugin** server and not a WorkState tool: WorkState runs in a container; launching `claude`
needs the host's CLI, the worktree on disk and the user's Claude login. A stdio server that Claude
Code starts for the session is a host process and has all three (doc 12 §1).

## 2. WorkState: per-session tokens

All new; additive; **not** under `/api/v1` (v1 is read-only by rule).

| Call | Who | Does |
|---|---|---|
| `POST /api/agent-sessions` `{name, role, launchedBy?, origin?, cwd?, externalRole?, claudeSessionId?, ttlHours?, replace?}` | a bearer with **orchestrator or controller** role (static or minted) | creates/updates the `agent_session` row for `name`, issues a token `wss_<base64url 32 bytes>`, stores only its SHA-256, returns `201 {sessionId, name, role, token, expiresAt}` **once** |
| `PATCH /api/agent-sessions/{id}` `{claudeSessionId}` | the same principal, or a controller | sets the Claude session uuid (history kept as an event, doc 11) |
| `DELETE /api/agent-sessions/{id}` | the same principal, or a controller | revokes: `revoked_at = now()`, live claims of that session released (`release_reason = 'revoked'`) |

Rules:
- **A role is never raised:** requested role ≤ the caller's role. Worker < orchestrator < controller.
- **One live token per name:** a second mint for a name with an unrevoked, unexpired token is `409`
  unless `replace: true`, which revokes the old one first. `replace` keeps the **same row and
  `sessionId`** (one row per name across restarts, one continuous history) and swaps only the token;
  it does not release claims. A revoked or expired name mints again without `replace`, on the same row.
- **Launcher collision (doc 11):** a name held by a row with a different `launchedBy` is `409`, and
  `replace` does not override it. A mint for a header-named row with the same launcher attaches the
  token to that row.
- `externalRole` fills `external_role` exactly as `X-WorkState-Foreman-Role` does.
- **"The same principal":** a controller may PATCH/DELETE any session. Otherwise, a session minted by
  a minted token belongs to that minting session. A session minted by a static token belongs to any
  static caller whose role is at least the granted role, because the static orchestrator and
  controller tokens carry no session and cannot be told apart beyond their role.
- **Revoke** also returns the released claims' `in_progress` items to `ready` and journals
  `claim_revoked`, as the expiry sweep does for an expired claim.
- Revoked, expired and unknown tokens are refused on `/mcp` itself (HTTP 401), before any tool runs.
- **Expiry:** `ttlHours` (default 72, 1..720) is an **idle window**, stored per row. Each successful
  authentication slides `token_expires_at` to now + that window, written at most once a minute per
  token; there is **no hard cap**, so a token in use never lapses (owner decision 2026-10-26: a token
  on disk exposes no read danger here). An unused token lapses when its window passes, and an expired
  or revoked token is never renewed — it is refused like an unknown one, however often presented. A
  row minted before the window was stored slides by 72. `expiresAt` in the mint response is the
  current expiry, not a deadline.
- **Authentication:** the bearer handler accepts the three static tokens as today, and a `wss_` token
  by hash lookup (not revoked, not expired). A minted token's principal carries the granted role
  (with the nesting the static tokens have) **and the session** — `CallerSession` takes the session
  from the principal and ignores an `X-WorkState-Agent` header on that request. The name is then
  bound to the credential, not asserted by a header (doc 12 §7.1, SEP-2567's "authenticated principal").
- Schema: `agent_session` gains `token_hash` (unique, nullable), `granted_role`, `token_expires_at`,
  `revoked_at`, `minted_by_session_id` (nullable). One additive migration with a working Down.
  A second, `TokenSlidingExpiry`, adds `token_ttl_hours` (nullable int, the idle window); its Down
  drops only that column, leaving tokens valid and sliding by the default.

## 3. The `agents` stdio server (plugin)

`plugins/rlarch/.mcp.json` → server `agents`: `node ${CLAUDE_PLUGIN_ROOT}/skills/nested-claude/server/agents.mjs`.
Tools appear as `mcp__plugin_rlarch_agents__<tool>`. No npm dependencies; plain ES modules with
`// @ts-check`, like `ts-source`. Speaks MCP over newline-delimited JSON-RPC on stdio.

### `agent_launch`

| Param | Type | Default | Maps to |
|---|---|---|---|
| `name` | string | required | `--name`; the `SendMessage` address |
| `brief` | path | required | a file inside `repo_root`; the protocol header is prepended and the result written to `.claude/briefs/brief-<name>.txt` |
| `repo_root` | path | required | must be a git working tree (refused otherwise); the session's cwd |
| `controller` | string | required | `"<name> [<6-hex ref>]"` (refused without the ref) — goes into the protocol header |
| `permission_mode` | `acceptEdits\|auto\|bypassPermissions\|manual\|dontAsk\|plan` | `auto` | `--permission-mode` |
| `toolset` | `none\|readonly\|fix` | `fix` | the allow-list profiles of `Start-NestedAgent.ps1`, plus `mcp__workstate` when `workstate.role` is not `none` (else the first `work_*` call parks the agent under `acceptEdits`) |
| `allowed_tools` | string[] | `[]` | merged into `--allowedTools` |
| `disallowed_tools` | string[] | `[]` | `--disallowedTools` |
| `add_dirs` | path[] | `[]` | `--add-dir` |
| `model` | string | — | `--model` |
| `effort` | `low\|medium\|high\|xhigh\|max` | — | `--effort` |
| `settings` | path or JSON string | — | `--settings` |
| `append_system_prompt` | string | — | `--append-system-prompt` |
| `workstate` | `{role: worker\|orchestrator\|controller\|none, env_file, url?, ttl_hours?}` | `{role: "worker"}` | §1/§2 |
| `dry_run` | bool | false | everything except minting and spawning; returns the argv (token shown as `<minted>`) |

Every variadic flag is closed by the next flag and the prompt goes last, exactly as the scripts do
(nested-claude §1, "Traps"). Arguments are passed as an argv array (no shell), so quoting traps are
gone. If `workstate.role` is not `none` and minting fails (no env file, server down, 4xx), the launch
is **refused** with the reason — never silently launched with the inherited controller registration.

### `agent_stop` `{id | name, remove?, keep_brief?}`
`claude stop` (and `rm` with `remove`), revoke the minted token, delete `mcp-<name>.json` always (it
holds a secret; the brief does not). State of launched agents is kept in `${CLAUDE_PLUGIN_DATA}`
(name, id, repo_root, WorkState session id, config path — never the token).

### `agent_list`
`claude agents --json` joined with that state: name, id, status, waitingFor, repo_root, WorkState
session and role.

### Results
Plain text plus structured content; refusals are tool results with `isError: true` that say what to
fix. No result ever contains a token.

## 4. What stays

- The `.ps1`/`.sh` launchers stay as the fallback (no plugin server, no Node) and as the reference for
  the argv order. The skill says the tool is the primary path.
- Header-named sessions (doc 11) keep working for registrations that cannot mint (Foreman today,
  human sessions with `--header X-WorkState-Agent`).
- `workstate-setup` still registers the user-level server with a static token. Making setup itself
  token-free is doc 12 §7.4, not in this unit.

## 5. Verification the controller runs before release

1. A real `agent_launch` from this session with `permission_mode: "acceptEdits"`, `workstate.role:
   "worker"`: the child's `work_brief` shows no "session unnamed" line; its writes are recorded under
   its name with `run_by = worker`; it cannot see `work_status`.
2. `agent_stop` → the token is revoked (a call with it is 401) and the config file is gone.
3. No token in: the tool result, the brief, the controller's transcript, `claude agents --json`.
