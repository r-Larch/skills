# 10 — Read API v1

Owner: skills-5d. Reviewed by autowork-7c (Foreman), 2026-10-22. Unit `workstate/mature/api`.
**Frozen at v1**: from here, additive only (rule 4).

One JSON contract for two clients: the :5290 dashboard and Foreman (native gpui). Read-only — the
MCP tools are the agents' only write path. The pre-v1 routes (`/api/projects`, `/api/watch`,
`/api/runs/{ref}/tree|brief|journal`) were removed once the new dashboard stopped calling them (unit
`mature/prev1-removal`). Outside v1, `/api` keeps `POST|DELETE /api/session` (sign-in),
`/api/stream`, `/api/agent-sessions` (doc 14, bearer only) and the dashboard's one write:

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

## Rules

1. **URLs take ids, never refs.** `/api/v1/items/{id}`. A ref is a label, not an address: the live data
   has `autowork/backlog` (a run) and `m0/backlog` (a phase), and the pre-v1 `/api/runs/backlog/tree`
   404'd on exactly that. A ref a human typed becomes an id through `GET /api/v1/resolve?ref=`.
2. **Every item, wherever it appears, is an `ItemRef`:**
   `{ id, ref, qualifiedRef, kind, title, status }`. `qualifiedRef` is always present: equal to `ref`
   when the ref is unique in the workspace, otherwise `run/ref` (the same rule the MCP text uses) —
   and when `run/ref` is itself shared (two projects with a run of the same name), the shortest
   project-qualified form that resolves to it alone, `project/ref` or `project/run/ref`.
3. **Instants are ISO-8601 with offset.** No "seconds remaining". Every response body carries `asOf`
   (server clock) and `seq` (the newest journal seq the response reflects), so a client can tell
   whether an SSE frame is already reflected.
4. **Additive only after freeze.** New fields and endpoints may appear; nothing is renamed or removed
   without `/api/v2`. Clients ignore unknown fields. Every response carries the header
   `X-WorkState-Api: 1`.
5. **Auth — any of:** the dashboard cookie; `Authorization: Bearer <token>` with any agent token
   (controller/orchestrator/worker); or a **reader** token — a new token kind that can call nothing but
   `GET /api/v1/*` and `/api/stream` (no MCP tools at all). Foreman authenticates with a reader token.
6. **Enums are lower_snake strings.** `status` is exactly `ItemStatus`:
   `open|ready|in_progress|blocked|review|done|cancelled|superseded`. There is no `deferred`: a deferral
   is `done` with resolution `obsolete` plus a `derived_from` follow-up elsewhere.
   `resolution` is exactly `ItemResolution`:
   `accepted|reworked|replanned|escalated|skipped|wontfix|duplicate|obsolete`. A forced close is not a
   resolution; it is the `forced` flag (from the `forced_transition` record).

## Shared shapes

```jsonc
ItemRef = { "id": "uuid", "ref": "2.3", "qualifiedRef": "m1/2.3", "kind": "task", "title": "…", "status": "done" }
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
            "expiresAt": "…", "expired": false }
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
    "runs": [{ "item": ItemRef, "rollup": Rollup, "branch": "feat/x" | null, "worktree": "P:/…" | null,
               "activeClaims": 1, "lastEventAt": "…", "inboxCount": 2,
               "lastVerification": { "passed": true, "at": "…" } | null }]
  }],                           // newest lastActivityAt first, then ref
  "workingNow": [Claim],        // live and expired-unreleased claims, newest first
  "inboxCount": 3,
  "honesty": { "doneUnverified": 0, "forced": 0, "ungated": 0, "followupsOpen": 4 } }
```

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

### `GET /api/v1/items/{id}/brief?for=resume|worker`
`text/plain`: the verbatim `work_brief` an agent would read. A secondary tab, not the main view.

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
{ "asOf": "…", "seq": 1234, "sessions": [{ "session": Session, "claims": [Claim] }] }
```

### `GET /api/v1/resolve?ref=backlog`
`{ "asOf": "…", "seq": 1234, "matches": [ItemRef] }` — zero, one or many; the client decides.

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
