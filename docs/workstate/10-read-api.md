# 10 — Read API v1

Owner: skills-5d. Reviewed by autowork-7c (Foreman), 2026-10-22. Unit `workstate/mature/api`.
**Frozen at v1**: from here, additive only (rule 4).

One JSON contract for two clients: the :5290 dashboard and Foreman (native gpui). Read-only — the
MCP tools are the agents' only write path. The pre-v1 routes (`/api/projects`, `/api/watch`,
`/api/runs/{ref}/tree|brief|journal`) were removed once the new dashboard stopped calling them (unit
`mature/prev1-removal`). Outside v1, `/api` keeps `POST|DELETE /api/session` (sign-in), the cookie's
tenant (`GET /api/session/tenants`, `POST /api/session/tenant`, below), its user (`GET
/api/session/me`) and password (`POST /api/session/password`), `/api/stream`, `/api/agent-sessions`
(doc 14, bearer only), the admin API (`/api/admin/*`) and the invite and reset links
(`/api/invite/{token}`, `/api/reset/{token}`), a project's members (`/api/projects/{id}/members`),
all below, and the dashboard's two writes:

### `POST /api/projects/{id}/archive` · `POST /api/projects/{id}/unarchive` (outside v1)

Archives or restores a project (doc 02 "Archived projects"). **Cookie only**, like `/api/session`:
a bearer token — agent or reader — is never evaluated here and gets 401; an agent archives with
`work_update(archived:)`. **Cross-site guard:** the request must carry the header
`X-WorkState-Write` (any value), else 403 and nothing is written. A cross-site form post cannot set
a header, and a cross-origin `fetch` that sets one is preflighted, which this host answers without
CORS — so the header proves the dashboard's own origin. A header rather than an antiforgery token
because it needs no token endpoint and no state, and the cookie is `SameSite=Strict` besides.
Answers `200 { "id", "ref", "archivedAt": "…" | null, "changed": true }`; 404 for an unknown id;
400 when the id is not a project. Journals `archived` / `unarchived` with the operator's name, role
`human`.

### `GET|POST /api/projects/{id}/members` · `DELETE /api/projects/{id}/members/{user}` (outside v1)

A project's members (doc 02 §12b, doc 08 §4 "Projects"), for the dashboard's sharing. **Cookie only**
— a bearer token gets 401 — in the cookie's workspace and no other (C62); writes carry the
`X-WorkState-Write` guard. Allowed to a member of the project and to an admin of the workspace (a
system administrator is one of every workspace). **Anyone else gets the same `404 {error}` as an id
that is no project** — a member of the workspace who is not in the project, a user of another
workspace — so a private project's existence is not revealed. An admin manages the members of a project
they are not in, and still reads none of its work (rule 8).

| Route | Answers |
|---|---|
| `GET /api/projects/{id}/members` | `200 { projectId, ref, everyone, members: [{ userId, name, addedAt, addedBy, disabledAt }], candidates: [{ userId, name, disabledAt }] }` — `candidates` are the workspace's members not in the project, `everyone` is true when there are none; both by name |
| `POST /api/projects/{id}/members` `{userId}` · `{all: true}` | `200` the same shape, after adding one member of the workspace, or every one not yet in; `400` for neither or both; `404` when the user is not a member of the workspace. Adding someone already in is `200`, unchanged |
| `DELETE /api/projects/{id}/members/{user}` | `204`; `404` when the user is not a member of the project; `409` removing the last member |

Each change journals `member_added` / `member_removed` on the project with the users' names, role
`human`. Removing a user from the workspace (`DELETE /api/admin/tenants/{id}/members/{user}`) is
`409` too while they are the last member of one of its projects, and names them.

### `POST /api/sessions/{id}/release` (outside v1)

The Sessions page's Release (doc 11 "Releasing a name"): frees the session's name so the next mint
creates the session fresh. Cookie only and the same `X-WorkState-Write` guard as archiving; a
bearer token is never evaluated here (a controller uses `POST /api/agent-sessions/{id}/release`,
doc 14 §2). Answers `200 { "sessionId", "name", "releasedAs" }`; 404 for an unknown id; 409 with the
refs when the session holds live claims, and 409 for a row already released. Journals
`session_name_released` with the operator's name, role `human`.

### `GET /api/session/tenants` · `POST /api/session/tenant` (outside v1)

The dashboard cookie's tenant (rule 7). **Cookie only**; a bearer token gets 401. `GET` lists the
workspaces the signed-in user may enter — their memberships in the order granted, then, for a system
administrator, every other workspace, oldest first:
```jsonc
[{ "id": "uuid", "slug": "default", "role": "admin" | "member" | "system-admin", "current": true }]
```
`POST {"id": "uuid"}`, with the same `X-WorkState-Write` guard as archiving, re-issues the cookie in
that workspace: `204`, or `403 {error}` unless the user is a member of it or a system administrator (an
id that is no workspace is the same 403). A sign-in lands in the user's first membership — a system
administrator with none, in the oldest workspace — and a user with no membership cannot sign in (`403
{error}`). A user whose membership of the cookie's tenant is removed is signed out on their next request
(`401`). The sidebar's workspace switcher calls it (doc 13).

### `GET /api/session/me` (outside v1)

Cookie only. `200 { "id", "name", "systemAdmin": bool, "administers": bool }` — `administers` is
true for a system administrator and for an admin of any workspace. It decides only what the sidebar
offers (the Admin link); the admin API judges every call itself.

### `POST /api/session/password` (outside v1)

Cookie only, with the `X-WorkState-Write` guard, rate-limited like sign-in (5 a minute per client, its
own window). `{ "current", "next" }`: `204` when `current` is the user's password, the cookie
re-issued with the user's new security stamp; a bare `401`, as sign-in gives, when it is not (the
dashboard tells it from a lapsed session by asking `GET /api/session`); `400 {error}` for a `next`
under 12 characters. The user's other cookies are signed out at their next request (doc 08 §4
"Passwords"); OAuth tokens and minted sessions are not.

### `/api/admin/*` (outside v1)

The Admin page's API (doc 08 §4 "Administration"). **Cookie only** — a bearer token gets 401 — and
every write carries the `X-WorkState-Write` guard. Each call is judged against the caller's rights as
the table has them on that request: a user who administers nothing gets `403 {error}` on every route;
a tenant admin gets 403 for a workspace that is not theirs (asked before whether it exists); a system
administrator gets 404 for an id that is no workspace. Errors are `{ "error": "…" }` and say what to do.

| Route | Who | Answers |
|---|---|---|
| `GET /api/admin/tenants` | admin, system admin | `[{ id, slug, createdAt, members, admins, pendingInvites, current }]` — every workspace for a system administrator, the ones they are an admin of otherwise, oldest first |
| `POST /api/admin/tenants` `{slug}` | system admin | `201` the same shape; `400` for a slug unlike `default` (lowercase letters and digits, words joined by single hyphens, ≤ 63); `409` when it exists |
| `GET /api/admin/tenants/{id}/members` | admin of it | `[{ userId, name, role, systemAdmin, joinedAt, disabledAt }]`, admins first |
| `PUT /api/admin/tenants/{id}/members/{user}` `{role}` | admin of it | `200 { userId, role, changed }`; `400` for a role other than `admin` / `member`; `404` when the user is not a member; `409` demoting the last admin |
| `DELETE /api/admin/tenants/{id}/members/{user}` | admin of it | `204`, and the user's project memberships there end with it; `404`; `409` removing the last admin, or the last member of one of its projects (named) |
| `GET /api/admin/tenants/{id}/invites` | admin of it | the unused, unexpired invites, newest first: `[{ id, role, createdBy, createdAt, expiresAt }]` — never a token |
| `POST /api/admin/tenants/{id}/invites` `{role}` | admin of it | `201 { id, token, url, role, tenant, expiresAt }` — the **only** answer that carries the token; `url` is `<origin>/join/<token>` |
| `DELETE /api/admin/tenants/{id}/invites/{invite}` | admin of it | `204`; `404` when that workspace has no such unused invite |
| `GET /api/admin/users` | system admin | `[{ id, name, systemAdmin, createdAt, disabledAt, tenants: [{ id, slug, role }] }]`, by name |
| `POST /api/admin/users/{id}/disable` · `/enable` | system admin | `200 { id, disabledAt }`; `404`; `409` disabling oneself |
| `POST /api/admin/users/{id}/reset-link` | system admin; an admin of every workspace the user is in, for a user who is no system admin | `201 { userId, name, token, url, expiresAt }` — the only answer that carries the token; `url` is `<origin>/reset/<token>`, valid 24 hours, replacing the user's unused older link; `403` for anyone else's user, an unknown id included; `404` an unknown id, to a system admin |

### `GET /api/invite/{token}` · `POST /api/invite/{token}/accept` (outside v1)

The invite page's two calls (doc 08 §4 "Invite links"). **No session needed**, rate-limited per
client (10 a minute, `429` with `Retry-After`). `GET` answers `200 { tenantId, tenant, role,
expiresAt, signedInAs, member }` — `signedInAs` the dashboard cookie's user, if the request carries
one, and `member` whether they already are one. `POST`, with the `X-WorkState-Write` guard, takes
`{ name, password }` without a session and nothing with one: `200 { name, tenantId }` with the
cookie set in that workspace; `400 {error}` for a blank name or a password under 12 characters; `409
{error}` for a name already taken (the link stays unused). A used, revoked, expired or unknown token
is `404 {error}` on both.

### `GET /api/reset/{token}` · `POST /api/reset/{token}` (outside v1)

The reset page's two calls (doc 08 §4 "Passwords"), shaped like the invite's: no session needed,
rate-limited per client, the `POST` with the `X-WorkState-Write` guard. `GET` answers `200 { name,
expiresAt }`. `POST { password }` answers `200 { name }` once the password is set — every cookie of
that user is signed out, and the browser is not signed in — or `400 {error}` for a password under 12
characters. A used, replaced, expired or unknown token is `404 {error}` on both.

## Rules

1. **URLs take ids, never refs.** `/api/v1/items/{id}`. A ref is a label, not an address: the live data
   has `autowork/backlog` (a run) and `m0/backlog` (a phase), and the pre-v1 `/api/runs/backlog/tree`
   404'd on exactly that. A ref a human typed becomes an id through `GET /api/v1/resolve?ref=`.
2. **Every item, wherever it appears, is an `ItemRef`:**
   `{ id, ref, qualifiedRef, kind, title, status, fullRef }`. `qualifiedRef` is always present: equal to `ref`
   when the ref is unique in the workspace, otherwise `run/ref` (the same rule the MCP text uses) —
   and when `run/ref` is itself shared (two projects with a run of the same name), the shortest
   project-qualified form that resolves to it alone, `project/ref` or `project/run/ref`.
   It is the callable form (`BoardQueries.CallableRefAsync`): passed to `work_get` / `work_brief` it
   resolves to that item alone. `fullRef` (added later, additive) is the full path
   `project/run/ref` — a run's is `project/run`, a project's is its ref, an item outside any
   project keeps its `qualifiedRef` — and resolves the same way. It stays unambiguous as runs are
   added, which the shortest form does not. The dashboard's copy buttons copy exactly this field
   and never compose a ref of their own.
3. **Instants are ISO-8601 with offset.** No "seconds remaining". Every response body carries `asOf`
   (server clock) and `seq` (the newest journal seq the response reflects), so a client can tell
   whether an SSE frame is already reflected.
4. **Additive only after freeze.** New fields and endpoints may appear; nothing is renamed or removed
   without `/api/v2`. Clients ignore unknown fields. Every response carries the header
   `X-WorkState-Api: 1`.
5. **Auth — any of:** the dashboard cookie; `Authorization: Bearer <token>` with any agent token
   (controller/orchestrator/worker); a **reader** token — a token kind that can call nothing but
   `GET /api/v1/*` and `/api/stream` (no MCP tools at all); or an **OAuth access token for the
   resource `<origin>/api`** with scope `workstate.read`. Foreman signs in with OAuth only (below).

   **OAuth on `/api`** (foreman-oauth). `/api` is a second protected resource beside `/mcp`
   (RFC 8707), with its own audience: a token for `<origin>/mcp` is `401` here, and a token for
   `<origin>/api` is `401` on `/mcp`. Authorize refuses a request naming both resources
   (`invalid_target`); ask for one. Its metadata is
   `GET /.well-known/oauth-protected-resource/api` → `{resource: "<origin>/api",
   authorization_servers: ["<origin>"], scopes_supported: ["workstate.read", "workstate.sessions",
   "offline_access"]}`, and an unauthenticated request to `/api/v1/*`, `/api/stream` or
   `/api/agent-sessions` is the usual `401` with an empty body plus
   `WWW-Authenticate: Bearer resource_metadata="<origin>/.well-known/oauth-protected-resource/api"`
   (a wrong static or `wss_` token still gets the bare `401`). Scopes: `workstate.read` for
   `GET /api/v1/*` and `/api/stream`, `workstate.sessions` for the agent-session group (doc 14 §2);
   a valid `/api` token without the route's scope is `403`. The scopes bind the OAuth principal only:
   the cookie, static tokens and `wss_` tokens need no scope. The user behind the token is the
   signed-in dashboard user, in the one tenant they approved it in (doc 08 §4), mapped to the
   controller role as on `/mcp`. Registration is a client-ID
   metadata document (an https `client_id`; Foreman's) or DCR (`POST /connect/register`, for
   development), public clients with PKCE S256, loopback callbacks on any port (RFC 8252 §7.3: a
   requested `http` loopback `redirect_uri` is admitted when a registered callback matches it in
   scheme, host, path and query — the registered port, or none, is ignored; `localhost` and
   `127.0.0.1` are different hosts; every other callback is exact, and the token request must name
   the `redirect_uri` the code was issued to, port included); the
   refresh token lives 30 days. Revoking the **refresh** token at `/connect/revoke` revokes the whole
   authorization (sign-out); revoking an access token revokes only that token.
6. **Enums are lower_snake strings.** `status` is exactly `ItemStatus`:
   `open|ready|in_progress|blocked|review|done|cancelled|superseded`. There is no `deferred`: a deferral
   is `done` with resolution `obsolete` plus a `derived_from` follow-up elsewhere.
   `resolution` is exactly `ItemResolution`:
   `accepted|reworked|replanned|escalated|skipped|wontfix|duplicate|obsolete`. A forced close is not a
   resolution; it is the `forced` flag (from the `forced_transition` record).
7. **The tenant.** Every request acts in one workspace, its credential's (doc 08 §4): the cookie's
   current tenant, a `wss_` token's session's, an OAuth token's (its `workstate:tenant` claim, else its
   user's only membership), or the one a static or reader token is bound to. No header or query
   parameter selects it. Everything a response names is in it: an id from another workspace is 404
   like a typo, `/resolve` and `/search` look nowhere else, and `/api/stream` carries only its events.
   A credential with no tenant — an OAuth token whose user is a member of several workspaces, or of
   none — is `403 {"error": "…"}`, the error saying what to do.
8. **The project.** Within its tenant, a request whose credential names a user sees the projects that
   user is a member of, and the workspace's projects no one has joined yet (doc 02 §15b, doc 08 §4); a
   static or reader token, which names none, sees every project of its workspace. To a user who is not a
   member, a project's items, runs and events do not exist, and the answer is the one a typo gets: an id
   of theirs is `404` with the unknown id's body, `/overview` and `/inbox` leave the project out,
   `/resolve` and `/search` find nothing in it, `/journal` without a scope leaves its events out, and
   `/api/stream` does not send them — read per event, so a member added sees the next one on a stream
   already open. Never `403`: a private project's existence is not revealed.

## Shared shapes

```jsonc
ItemRef = { "id": "uuid", "ref": "2.3", "qualifiedRef": "m1/2.3", "kind": "task", "title": "…", "status": "done",
           "fullRef": "workstate/m1/2.3" }
Rollup  = { "total": 14, "done": 10, "inProgress": 1, "review": 0, "blocked": 0, "open": 3,
            "cancelled": 0, "superseded": 0 }      // leaves only: task, followup, subtask, spike, question, incident
          // total = every leaf below, cancelled and superseded included (the sum of the seven counts);
          // the MCP text's "n/m done" denominator is total − cancelled − superseded
Session = { "id": "uuid", "name": "aw-m1", "role": "worker", "externalRole": "repo" | null,
            "origin": "foreman/nomos/Nomos" | null, "launchedBy": "skills-5d" | null,
            "claudeSessionId": "…" | null, "cwd": "…" | null, "tree": "…" | null,
            "client": "claude-code 2.x" | null, "firstSeenAt": "…", "lastSeenAt": "…" }
          // doc 11: a session exists only when named by the X-WorkState-Agent header;
          // unnamed calls carry "session": null and are identified by actor/role alone
Claim   = { "item": ItemRef, "run": ItemRef, "session": Session | null, "agent": "controller",  // agent = legacy label
            "role": "worker", "tree": "P:/…/m1-tool-args" | null, "acquiredAt": "…", "heartbeatAt": "…",
            "expiresAt": "…", "expired": false,
            "heldBySession": "aw-m4-2b" | null }
          // heldBySession: the holding session's name (claim.session_id → agent_session.name), so a
          // client shows "aw-m4-2b · controller" without reaching into session; null for a claim
          // taken without a session (a static token with no X-WorkState-Agent), shown by role alone.
          // Every Claim carries it: overview workingNow, board rows, the item page, /sessions.
JournalEvent = { "seq": 1, "at": "…", "item": ItemRef | null, "run": ItemRef | null, "session": Session | null,
                 "actor": "controller", "role": "controller", "kind": "…", "summary": "…", "payload": { } }
```

## Endpoints

### `GET /api/v1/overview`
Everything a human needs on landing, in one call.
```jsonc
{ "asOf": "…", "seq": 1234,
  "projects": [{
    "item": ItemRef, "rollup": Rollup,
    "repos": [{ "kind": "remote" | "root", "key": "github.com/r-larch/skills" }],   // doc 02 project_repo; may be []
    "archivedAt": "…" | null,        // doc 02; archived projects are listed, the client hides them
    "lastActivityAt": "…" | null,    // newest journal event in the project's subtree; null = none yet
    "openRuns": 2,                   // runs not done, cancelled or superseded
    "sharing": { "members": 2, "you": true, "names": ["ana", "ben"] },   // added for sharing, additive
    "runs": [{ "item": ItemRef, "rollup": Rollup, "branch": "feat/x" | null, "worktree": "P:/…" | null,
               "activeClaims": 1, "lastEventAt": "…", "inboxCount": 2,
               "lastVerification": { "passed": true, "at": "…" } | null }]
  }],                           // newest lastActivityAt first, then ref
  "workingNow": [Claim],        // live and expired-unreleased claims, newest first
  "inboxCount": 3,
  "honesty": { "doneUnverified": 0, "forced": 0, "ungated": 0, "followupsOpen": 4 } }
```

`sharing` (added for the dashboard's sharing marker, additive) is who shares the project (doc 02 §12b):
`members` its member count, `0` when no one has joined it and so it is open to the whole workspace; `you`
whether the caller is one of them — false for such a project, and always false for a static or reader
token, which names no user; `names` the members by name (case-insensitive order), so the marker names them
without a call per project. Read in one statement under the caller's own tenant transaction, so it says
nothing about a project rule 8 leaves out — the response does not list one, and its member rows are not read.

### `GET /api/v1/runs/{id}/board`
The run as one flat list, **pre-order by seq, stable** (a parent precedes its children; siblings in
seq order). The client rebuilds the tree from `parentId`/`depth`/`hasChildren`.
```jsonc
{ "asOf": "…", "seq": 1234,
  "run": ItemRef, "project": ItemRef, "intent": "…", "branch": "…" | null, "worktree": "…" | null,
  "rollup": Rollup,
  "rows": [{
    "item": ItemRef, "parentId": "uuid", "depth": 1, "seq": "1.5", "hasChildren": false,
    "resolution": "accepted" | null, "priority": 0, "severity": "blocker" | null,
    "criteria": { "checked": 3, "total": 4 },
    "rollup": Rollup | null,                  // containers only
    "claim": Claim | null,
    "commit": "a439808" | null,               // latest commit artifact
    "verification": { "passed": true, "runBy": "orchestrator", "at": "…" } | null,   // latest
    "blockedBy": [ItemRef],                   // open blockers only
    "flags": ["disagrees","done_unverified","forced","stale_claim","unconsumed_notes"],
    "openedAt": "…", "startedAt": "…" | null, "closedAt": "…" | null }] }
```

### `GET /api/v1/items/{id}`
A unit page: what was asked, what the agent **reported**, what was **verified**.
```jsonc
{ "asOf": "…", "seq": 1234,
  "item": ItemRef, "path": [ItemRef],          // project → … → parent
  "intent": "…", "verifyCmd": "…" | null, "riskTags": [], "priority": 0, "severity": null, "source": null,
  "resolution": null, "closedBy": null, "openedAt": "…", "startedAt": "…" | null, "closedAt": null,
  "criteria": [{ "id": "c1", "text": "…", "checked": true, "evidence": "…" | null, "checkedAt": "…" | null }],
  "claim": Claim | null,
  "reports": [{                                // work_report: what the agent SAID
    "attempt": 1, "session": Session | null, "agent": "…", "startedAt": "…", "endedAt": "…" | null,
    "status": "done", "files": ["…"], "verify": "…", "redCheck": "…", "deviations": "…", "risks": "…",
    "notDone": "…", "at": "…" }],
  "verifications": [{                          // work_verify: what was PROVEN
    "command": "…", "resultLine": "Passed! 308", "passed": true, "counts": { } | null,
    "runBy": "orchestrator", "session": Session | null, "redCheck": "…" | null, "at": "…" }],
  "artifacts": [{ "kind": "commit", "value": "a439808", "at": "…" }],
  "children": [{ "item": ItemRef, "criteria": { "checked": 1, "total": 2 } }],
  "findings": [{ "item": ItemRef, "severity": "blocker", "source": "review" }],   // against this item
  "notes": [{ "id": "…", "kind": "decision", "body": "…", "from": ItemRef | null, "to": ItemRef | null,
              "consumedAt": null, "createdAt": "…", "session": Session | null }],
  "links": { "out": [{ "type": "blocks", "item": ItemRef }], "in": [{ "type": "blocks", "item": ItemRef }] },
  "assumptions": [{ "ref": "A1", "text": "…", "status": "open", "revisitWhen": ItemRef | null }],
  "events": [JournalEvent],                    // newest 50 for this item, ascending
  "eventsHasMore": true }
```

### `GET /api/v1/items/{id}/brief?for=resume|worker|gate`
`text/plain`: the verbatim `work_brief` an agent would read. A secondary tab, not the main view.
`gate` renders `work_brief(for:"gate")` — on anything but a phase, its refusal text — and any other
`for=` is a 400 naming the three.

### `GET /api/v1/inbox?project={id}` (project optional)
Everything waiting on a **human**, newest first.
```jsonc
{ "asOf": "…", "seq": 1234, "rows": [{
    "key": "blocked:uuid",           // stable: kind + ":" + item id; a client may remember dismissals by it
    "kind": "blocked|blocker_finding|question|expired_claim|assumption_due|done_unverified|stale_in_progress|unconsumed_note",
    "item": ItemRef, "run": ItemRef, "what": "…", "since": "…",
    "call": "work_status(ref:\"ws/12\", to:\"in_progress\")" }] }   // the resolving MCP call, qualified
```
An item that is open and waiting on another item's `blocks` edge is **not** an inbox row: it waits on
work, not on a person.

### `GET /api/v1/journal?scope={id}&since={seq}&before={seq}&limit=&kinds=`
Ascending by seq. `kinds` comma-separated:
`transition,verification,report,finding,note,claim,link,created,carried`.
```jsonc
{ "asOf": "…", "seq": 1234, "cursor": 1234, "hasMore": false, "events": [JournalEvent] }
```
- No `since` (or `since=0`): the newest `limit` events. `since={seq}`: the oldest `limit` events after
  it; `cursor` is the newest seq returned (the next `since`), `hasMore` says more follow.
- `before={seq}` ("load older"): the newest `limit` events with seq below it, still ascending, in one
  call; `cursor` is the oldest seq returned (the next `before`, or `before` itself when the page is
  empty) and `hasMore` says older events remain. A `before` past the newest seq is the newest page.
  Same `scope` and `kinds` filters. `since` and `before` together is a 400.

### `GET /api/v1/backlog?project={id}`
Open follow-ups in the project-level backlog run, grouped by origin (`derived_from` → origin run).
```jsonc
{ "asOf": "…", "seq": 1234, "run": ItemRef | null, "groups": [{ "origin": ItemRef | null, "items": [ItemRef] }] }
```

### `GET /api/v1/sessions?active=true&name=&claudeSessionId=`
Sessions (doc 11), most recently seen first. `active=true` limits to the last 30 minutes; `name` and
`claudeSessionId` filter to exact matches (Foreman's fleet rows join on them).
```jsonc
{ "asOf": "…", "seq": 1234, "sessions": [{ "session": Session, "claims": [Claim], "token": "none" | "live" | "revoked" | "expired" }] }
```
`token` (added for the Sessions page, additive) is the row's minted token: `none` for a session only
ever named by header, else whether it is live, revoked or expired. A released row (doc 11) is listed
under its tombstone name with `revoked`.

### `GET /api/v1/resolve?ref=backlog`
`{ "asOf": "…", "seq": 1234, "matches": [ItemRef] }` — zero, one or many; the client decides.

### `GET /api/v1/search?q=&limit=`
The dashboard's find (doc 13 "The command palette"), over items only. `limit` defaults to 20, at
most 50. Hits come in three stages, and an item appears once, at its first: **`ref`** — `q` resolves
to it by `/resolve`'s rule (`spa-blank-page-seam`, `m/12`, `project/run/ref`; every match of an
ambiguous ref); **`prefix`** — its ref starts with `q`, ignoring case, taken literally (`%` and `_`
are not wildcards), shortest ref first; **`text`** — full text over ref, title and intent, best rank
first. Only the request's workspace. 400 when `q` is missing or blank.
```jsonc
{ "asOf": "…", "seq": 1234,
  "hits": [{ "item": ItemRef, "run": ItemRef | null, "project": ItemRef | null,
             "match": "ref" | "prefix" | "text", "snippet": "…" | null }] }
```
`run` and `project` are the nearest run and project at or above the item (a run's `run` is
itself, a project's `project` is itself), so a client links and labels a hit without another read.
`snippet` is plain text — the matching fragment of the intent, else the title — on a `text` hit only.

### `GET /api/v1/prompts?scope={id}`
The prompt actions that apply to one project, run or item (doc 13 "The prompt catalogue"), in the
catalogue's fixed order; an action that does not apply is absent. Each `text` is the whole prompt a
fresh session is started with — never shortened. `count` is `null` for an action without one.
Read-only like every v1 route: it neither sweeps lapsed claims nor consumes notes (unlike `/brief`).
400 without `scope`, 404 for an unknown id.
```jsonc
{ "asOf": "…", "seq": 1234, "scope": ItemRef,
  "prompts": [{ "key": "housekeeping", "label": "Housekeeping", "count": 7,
                "text": "Housekeeping for WorkState project …" }] }
```

### `GET /api/stream`
SSE, unchanged transport. An event means "re-read"; its data is
`{ "seq": 1235, "runId": "uuid" | null, "itemId": "uuid" | null, "kind": "transition" }`.
