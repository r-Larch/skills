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

`0.6.0` (rlarch 2.4.0) carries session self-naming (the client key, `work_session`) and the bare
`work_brief()` project chooser.

`0.7.0` (rlarch 2.5.0) carries the gate brief (`for:"gate"`, also over the read API), `work_search`
over assumptions, rules and notes with `scope`, the raised `tools/list` budgets, the dashboard's
`fullRef` field, and two indexes (`item_ref_lower_idx`, `note_search_idx`).

`0.7.1` (rlarch 2.5.1) carries readable dashboard links (`/r/<project/run/ref>`, the `web` line in
`work_brief` and `work_get`, optional `WORKSTATE_PUBLIC_URL`) and the dashboard's split run page.

`0.8.0` (rlarch 2.7.0) carries OAuth 2.1 sign-in for `/mcp` (OpenIddict; client-ID metadata
documents and dynamic registration; PKCE S256; keys held in Postgres), the `/connect/signin` and
`/connect/consent` pages with the client's icon, `WORKSTATE_TRUSTED_PROXIES`, and one additive
migration (`OAuthServer`). Static and minted `wss_` tokens are unchanged.

`0.9.0` (rlarch 2.7.0, no plugin change) carries OAuth for the read API, `/api/stream` and the
agent-session mint (resource `<origin>/api`, scopes `workstate.read` and `workstate.sessions`), the
cascade that ends `wss_` sessions minted under a revoked authorization, the launcher takeover of a
name by Claude session id, the name release (dashboard Sessions page and
`POST /api/agent-sessions/{id}/release`), and one additive migration (`OAuthMintedSession`).

`0.9.1` (no plugin change, no migration) lets a registered client sign in on any port of a
registered `http` loopback callback (RFC 8252 §7.3), for dynamically registered clients as well as
metadata-document ones; until then only the latter had it.

`0.9.2` (no plugin change, no migration) applies the same rule to the consent page: its
`form-action` names the requested loopback origin, so Approve reaches the client's listener, and the
page no longer flags that callback as unregistered.

`1.0.0` (no plugin change) makes the server multi-tenant (§4): users and memberships, the request's
tenant from its sign-in only, row-level security, OAuth tokens and agent sessions per user and
tenant, admin pages and invite links, password change and reset, and the Ctrl+P palette. Its seven
migrations (TenantRefs, Users, ClaimKeyWorkspace, AgentSessionUser, UserLinks, SecurityStamp,
TenantRls) add tables, columns, keys and policies; ClaimKeyWorkspace also backfills live claims, and
TenantRls marks four built-in functions LEAKPROOF (§4, doc 02 §15). After the upgrade the operator
signs in to the dashboard once, before the OAuth clients refresh: that sign-in creates the first
user, under whose name their tokens carry over.

`1.0.1` (no plugin change) numbers and keeps unique the refs of projects and runs among their
siblings (doc 02 "Ref scoping"): a ref-less run is `1`, `2`, … under its parent, a ref-less project
`project-1`, `project-2`, … in its workspace, and a duplicate is refused. Its one migration
(`ScopeRefUnique`) first renames each stored duplicate but the oldest to `<ref>-2`, `<ref>-3`, …,
journalled as `updated`, then adds the index `item_sibling_ref_unique`. It also issues refresh
tokens as opaque reference handles (§4), so a Windows client can keep one in Credential Manager.

`1.1.0` (no plugin change) makes projects private or shared (§4 "Projects"; doc 02 §12b, §15b): a
project's work is visible only to its members, enforced by row-level security with a fast path
keyed on `workspace.access_seq`, and the dashboard shows and manages members. Root repo keys and
path tree locks are per user. Its four migrations (ProjectMembers, ProjectAccess, ProjectAccessSeq,
MachineKeys) add `project_member` and make every workspace member a member of each existing project.
They also add the project policies, the access seq with its triggers, and per-user `project_repo`
rows and claim keys. Each existing root binding is copied to every user who can see its project.
`/api/stream` also carries the `X-WorkState-Api` header now.

`1.1.1` (no plugin change) fixes three gaps 1.1.0 left open. A claim records its holder's user, so a
claim made without a session is retaken or extended only by the same user. `work_move` enforces
`item_kind.allowed_parent_kinds`. Agent sessions are per user for a workspace member (doc 10
`/sessions`). Its one migration (`ClaimHolderUser`) adds `claim.holder_user_id` and backfills it on
live claims from their session's user.

`1.1.2` (no plugin change) applies `item_kind.allowed_parent_kinds` to create, move and import
alike. Only a project is a root. Its one migration (`TaskUnderTask`) lets a task sit under a task,
which 19 live rows already did. It also makes printed assumption refs callable, records a
question's answer when it closes, and gives the dashboard a phone layout.

`1.1.3` (no plugin change, no migration) adds Connected apps to the Account page: a user lists
the OAuth authorizations they hold in the workspace and revokes one, which also revokes the agent
sessions minted under it.

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
§2, bearer only), the dashboard's writes (`POST /api/projects/{id}/archive|unarchive` and the rest in
doc 10 "Outside v1", cookie only, with the `X-WorkState-Write` header), the admin API `/api/admin/*`
(cookie only) and the invite link `/api/invite/{token}` (anonymous, below) are the only other `/api`
routes.

| Token | Config / env | Reaches |
|---|---|---|
| worker · orchestrator · controller | `WorkState:Auth:{Worker,Orchestrator,Controller}Token` · `WORKSTATE_{WORKER,ORCHESTRATOR,CONTROLLER}_TOKEN` — **local install only** (required in `mcp/compose.yml`; the server deploy `deploy/multi-deploy` sets none, so a server accepts none) | `/mcp` by role, and `GET /api/v1/*` + `/api/stream`; orchestrator and controller also `/api/agent-sessions` |
| reader | `WorkState:Auth:ReaderToken` · `WORKSTATE_READER_TOKEN` (**optional**: compose passes `${WORKSTATE_READER_TOKEN:-}`, and empty means no reader token exists) | `GET /api/v1/*` and `/api/stream` only — `/mcp` refuses it (403), and so does `/api/session` |
| minted `wss_…` (per session) | none — `POST /api/agent-sessions` with an orchestrator or controller bearer returns it once; the store keeps only its SHA-256. Expires after `ttlHours` (default 72, max 720) **unused** — an idle window each use renews, no hard cap — revoked by `DELETE` | exactly what a static token of its granted role reaches (never above its minter's role), and its calls are that session's: `X-WorkState-Agent` is ignored |

Foreman signs in with OAuth only (autowork m5/43): it registers by DCR, signs in with PKCE for the
scopes `workstate.read workstate.sessions offline_access`, and mints a `wss_` session per agent it
launches. It holds no reader token.

**The dashboard's users (m48).** The cookie is issued to a row of `app_user` (doc 02 §12a), not to the
configured credential. `WorkState:Dashboard:{Username,Password}` (`WORKSTATE_DASHBOARD_USERNAME` /
`_PASSWORD`) is a **bootstrap credential**: the first `POST /api/session` that presents it while
`app_user` is empty creates the first user — named after the configured username, the presented
password stored only as an ASP.NET Core `PasswordHasher` hash, system administrator, admin of every
workspace that exists then — on an empty database a new `default` one, so they have a tenant to sign in
to. Two first sign-ins at once create one user (`LOCK TABLE app_user`). From
then on only the table decides: **changing or removing the configured password changes nothing**, and
the dashboard keeps working with neither set. A wrong password, an unknown name and a disabled user
get the same 401 and each runs one hash verification. Every cookie request re-reads its user
(the `sub` claim): a user deleted or disabled (`disabled_at`) is signed out on their next request,
and so is a cookie whose security stamp (`workstate:stamp`) is no longer the user's
`app_user.security_stamp`, which a password change or reset draws anew (m52). A cookie issued before
m48 (no `sub`), before m50 (no tenant) or before m52 (no stamp) has to sign in again. The
configured credential has nothing to do with OAuth (m51): `/connect/authorize` is gated by a
signed-in cookie, and a fresh install with no user has none to present.
Users are managed on the dashboard's Admin page and change their own password on its Account page
(m52, below).

**Tenants (m50).** A tenant is a `workspace` row, and every request acts in exactly one: its
credential's, never one a header, a query parameter or a server default names (C62). Every
authentication path puts it on the principal as the claim `workstate:tenant`:

- **Dashboard cookie** — at sign-in, the user's first membership (oldest grant first); a system
  administrator who is a member of nothing lands in the oldest workspace; a user who is a member of
  nothing cannot sign in (403). `GET /api/session/tenants` lists the workspaces the user may enter and
  `POST /api/session/tenant {id}` re-issues the cookie in another, if they are a member of it or a
  system administrator (doc 10). The sidebar's workspace switcher (doc 13) calls it, shown only to a
  user who may enter more than one.
  Every cookie request re-checks: a user whose membership of the cookie's tenant is removed is signed
  out there on their next request.
- **`wss_` token** — its session's workspace. A mint creates the session in the minting credential's
  own tenant (doc 11). A session a user's credential minted works only while that user may enter its
  workspace: removing the membership or disabling the user fails the token on its next use (401).
- **OAuth access token** — the token's `workstate:tenant` claim (m51). A token belongs to **one user
  in one tenant**: its `sub` is the user's `app_user` id, and its tenant is the one the user's cookie
  was in when they approved. The consent page names that workspace and offers the user's others to
  switch to first; a user in several tenants signs in once per tenant, and each client's connection
  acts in one. An approval is remembered per user, client **and tenant** (the OpenIddict permanent
  authorization keeps the tenant in its `workstate_tenant` property): approved in A, the same client
  authorized from B asks again and gets a token for B. Every call and every refresh re-checks that
  the user exists, is not disabled and may still enter the tenant; otherwise the call is refused
  403 and the refresh `invalid_grant`, each saying to sign in again. The role is controller for a
  member, an admin and a system administrator alike: tenant roles decide who manages members, not
  what the work tools may do.
  **Legacy tokens.** A token issued before m51 names its user by name and no tenant (Claude Code,
  Foreman and `agent_signin` hold such refresh tokens). Presented as an access token it acts in the
  one workspace its user is a member of, refused 403 "sign in again" with none or several. Its
  refresh succeeds without a new sign-in while the user has exactly one membership, and returns a
  token of the new shape — the user by id, that tenant — under the same authorization, so revoking
  it still ends the connection. **This support ends once those tokens have refreshed**: every client
  that refreshes within the refresh token's 30 days holds a new-style token from then on.
  **Refresh tokens are opaque reference handles (m57)**: 43 characters naming a row of
  `openiddict_token`, which holds the encrypted payload; access tokens stay self-contained. Why:
  Foreman keeps its refresh token in Windows Credential Manager, whose blob (`CredWriteW`) takes at
  most 2560 bytes, and the self-contained refresh token of 1.0.0 — an encrypted JWT carrying the
  user, the tenant and the authorization — measured 2875 characters, so Foreman's refresh succeeded
  and storing its result failed. A self-contained refresh token issued before the switch still
  redeems, once, into a handle (OpenIddict tries a handle lookup only for a token that is not a JWT),
  so no client signs in again. Rotation, the 30-second reuse leeway for concurrent refreshes and
  revocation — of the token or of its authorization — behave the same on either format.
- **Static role tokens and the reader token** — bound to the workspace `WorkState:Workspace` names,
  or, unset, the oldest workspace. That slug is these credentials' own configured binding, not a
  default for anyone else: only their handler reads it. A configured workspace that does not exist
  yet is created at the token's first use, so a fresh install's first `work_create` has somewhere to
  write. **Static role tokens are for a local single-user install only** (René, shared-v2): the server
  deploy configures none, so on a shared server every client signs in with OAuth and `agent_launch`
  mints with the `agent_signin` grant (doc 14). A token string a machine still holds from before is a
  credential the server no longer accepts (401). The reader token stays optional on both.

A principal with no tenant — including a request carrying two credentials that name different
workspaces — gets `403 {"error": "…"}` saying what to do. No request creates its own tenant:
workspaces are created only by the first user's bootstrap (`default`, when there is none), by a
static token's first use (its configured slug, or `default` on an empty database), and by a system
administrator on the Admin page (`POST /api/admin/tenants`). A tree lock is
per tenant too: a path claimed in one workspace does not hold the same path in another (doc 07 §5).
Within one tenant a path is also per **user** (m60): it names a folder on one person's machine, so a
path lock and a repository's `root` binding are the signed-in user's own, and two users' same path are
two checkouts. A remote, a branch and the ref scope name the same thing for everyone and stay the
tenant's. A credential with no user — a static token on a local install — keeps the tenant-wide
behaviour (doc 02 §project_repo, §10).

**Administration (m52).** Roles govern administration only: every member of a workspace has full
work rights there, whatever its role. A **system administrator** (`app_user.is_system_admin`) sees
and manages every workspace, creates workspaces and lists, disables and re-enables users; a **tenant
admin** (`tenant_membership.role = 'admin'`) invites into their own workspace and changes or removes
its members; a **member** reaches no admin route (403). The admin API (doc 10 "Outside v1") names the
workspace it acts on in its path — administering a tenant is not acting in it, and a system
administrator administers all of them — so every call is judged against the caller's rights in that
workspace **as the table has them on that request**, never against the cookie's tenant or anything
the request says about its caller (C62): a tenant admin of A is refused (403) every read and write
aimed at B, and a workspace's id is checked before its existence, so they learn nothing about B. A
workspace keeps at least one admin: demoting or removing its last one is refused (409), under a lock
on the workspace row so two admins demoting each other at once cannot both succeed. A system
administrator cannot disable themselves. Disabling a user ends their cookies, their OAuth tokens and
the sessions they minted at the next request each makes; enabling them undoes it.

**Invite links (m52).** No email is sent: an admin creates a link and copies it to the person. It is
`<origin>/join/<token>`, the token 32 random bytes in base64url, **shown once** in the create
response; `user_link` keeps only its SHA-256 (C64), so a lost link is revoked and made again, never
shown again. An invite names a workspace and a role, is valid **14 days** and works **once**: it is
used by one `UPDATE user_link SET used_at = now() WHERE token_hash = … AND used_at IS NULL AND
expires_at > now() RETURNING …`, so of two requests racing with one token exactly one gets a row.
Accepting it with no session creates the user — a name unique ignoring case and a password of **at
least 12 characters** (and at most 1024), stored hashed — with that membership; with a signed-in
dashboard cookie it gives that user the membership instead (never lowering a role they already hold
there). Either way the browser is then signed in to the invite's workspace. A name already taken is
refused (409) and leaves the link unused. `GET /api/invite/{token}` and `POST
/api/invite/{token}/accept` take no session and are rate-limited per client address (10 a minute,
sign-in's window doubled because the page reads the link before it uses it); the accept also needs
the `X-WorkState-Write` header. A used, revoked, expired or unknown token is one answer, 404.

**Passwords (m52).** A password is at least **12 characters** (and at most 1024) wherever one is
set: accepting an invite, changing one's own, using a reset link. `POST /api/session/password
{current, next}` (the cookie and the `X-WorkState-Write` header) changes the signed-in user's
password; a wrong current password is sign-in's bare 401, and it is rate-limited like sign-in, in a
window of its own. Success rehashes and draws a new security stamp, which **ends every other cookie
of that user** at its next request, and re-issues the requesting cookie with the new stamp, so it
stays. **OAuth grants and minted `wss_` sessions stay**: their tokens are not cookies and carry no
stamp. A **reset link** is the invite's mechanism for one user: `POST
/api/admin/users/{id}/reset-link` returns `<origin>/reset/<token>` once (only the SHA-256 kept, C64),
valid **24 hours**, used once by the same conditional `UPDATE`; a newer link for the same user
replaces an unused older one. Using it (`POST /api/reset/{token} {password}`, no session needed,
rate-limited like an invite) sets the password and draws a new stamp, which **ends all of the
user's cookies**, the browser's own included; OAuth grants stay. A reset hands its holder the whole
account, so a system administrator makes one for anyone, and a tenant admin only for a user who is
no system administrator and every one of whose workspaces they administer — never for someone with
a membership in a workspace that is not theirs (C63); every refusal, an unknown id included, is the
same 403.

**Projects: private by default, shared by members (m58).** A project has a member list
(`project_member`, doc 02 §12b) — the people who may work in it. Every member has full read and
write; there is no read-only member. A project is **private to its creator** until shared: the user
whose OAuth token or minted `wss_` session ran `work_create` is its one member. A credential with no
user — a static token on a local install, or the offline importer — has no creator to name, so the
project gets **every current member of the workspace**, the same rule the migration applied to every
project that existed before members did. A user who is not a member of the workspace they act in (a
system administrator visiting it) cannot create a project there (m59): only a workspace's members can be
project members, so it would be hidden from its creator.
Sharing is the dashboard's: `GET|POST /api/projects/{id}/members`, `DELETE
/api/projects/{id}/members/{user}` (doc 10 "Outside v1"), allowed to a member of the project and to
an admin of its workspace (a system administrator is one of every workspace). Anyone else — a
member of the workspace who is not in the project, a user of another workspace — gets the 404 an
unknown id gets, so a private project's existence is not revealed. **Add everyone** (`{"all":
true}`) is how a project becomes tenant-wide; a member who joins the workspace later is not added
to its projects. A project keeps at least one member: removing its last is refused (409), and so is
removing that user from the workspace, naming the projects, until someone else is in each.
Otherwise leaving a workspace ends its project memberships with it; **disabling a user does not**
(they may be enabled again).

**Only a project's members reach it (m59, doc 02 §15b).** Membership decides every read and write, in
the database: a member of the workspace who is not in a project finds none of it — not in the overview,
the inbox, a search, the journal, the live stream, a `/r/` link or a ref that names it — and is answered
as for a ref or an id that does not exist. A default scope (`work_brief()`, `work_next()`) lands only in
a project the caller is in. There is no read-only member and no read-only share. A workspace admin
manages any project's members without being one, and reads no project they are not in. A project no one
has joined yet — a static token's on an install with no user — is every member's until its first member
joins. A credential with no user — a static role token or the reader token — sees every project of its
workspace; the server deploy configures no static role token, so there every client is a user.

**`WORKSTATE_PUBLIC_URL`** (`WorkState:PublicUrl`, **optional**; compose passes
`${WORKSTATE_PUBLIC_URL:-}`) is the base of the `web` line `work_brief` and `work_get` print
(doc 01 §8). Unset, the line names the scheme and host the MCP request came in on — from the host
that is `http://127.0.0.1:5290`, which is also where the dashboard is. Set it when humans reach the
dashboard at another address than agents reach `/mcp` (a reverse proxy, a LAN name). An `https`
public URL also makes the dashboard cookie `Secure` unconditionally; otherwise the cookie follows the
request's scheme.

**`WORKSTATE_TRUSTED_PROXIES`** (`WorkState:TrustedProxies`, **optional**; compose passes
`${WORKSTATE_TRUSTED_PROXIES:-}`) is a comma-separated list of IPs or CIDRs whose `X-Forwarded-Proto`,
`X-Forwarded-Host` and `X-Forwarded-For` the API believes. Set it to the reverse proxy's address (on
a docker network, that network's CIDR). From those addresses the request's scheme and host become
the forwarded ones before authentication runs, so the `web` line (when no public URL is set), the
`/r` redirects and the cookie's `Secure` flag all follow the `https` origin the proxy is reached on.
From any other address the headers are ignored. Empty, the default, trusts nobody and leaves the
forwarded-headers middleware out of the pipeline: a local install has no proxy, and a header any
client can send must not choose the origin. A malformed entry stops the API at startup.

These two are the only settings that differ between a local and a server install (rule C32): the
image, the port it listens on (8080) and the code path are the same. The reverse-proxy compose itself
(TLS, Traefik) is unit `shared-v1/deploy`.

### How to make it always-on

**Both composes have `restart: unless-stopped` on both services.** So, from a clone:

```
docker compose -f mcp/compose.yml up -d
```

…or, with no clone at all, `/rlarch:workstate-setup` (its script) writes `~/.workstate/compose.yml`,
which **pulls the published image** instead of building one, and runs the same command against it.

> **Changed 2026-10-26 — a published image.** `.github/workflows/workstate-image.yml` builds
> `mcp/Dockerfile` for `linux/amd64` and `linux/arm64` when a tag `workstate-v<version>` is pushed
> (since 2026-10-28; before that, on every push to `master` touching `mcp/**`). The tag must equal
> `ServerInfo.Version`, and a version already on ghcr is refused rather than rebuilt. It pushes `ghcr.io/r-larch/workstate-api:<ServerInfo.Version>` and `:latest`, labelled
> `org.opencontainers.image.source=https://github.com/r-Larch/skills`. The pull-only compose is
> `plugins/rlarch/skills/workstate-setup/assets/compose.yml`: the same services, volume and project
> name (`workstate`) as `mcp/compose.yml`, so a machine can move between them and keep its data.
> `mcp/compose.yml` stays the build-from-source compose for development. The Dockerfile's build
> stages run on the builder's platform and cross-target (`-a $TARGETARCH`): `dotnet restore` under
> QEMU aborts with exit 134.

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
  markdown files in git becomes the one artifact that is not in git. *(2026-10-26: scripted —
  `workstate-setup.sh --backup <file>` / `--restore <file>` run `pg_dump -Fc` / `pg_restore --clean`
  inside the `db` container; a restore writes a safety dump first. Not automated on a schedule.)*
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
