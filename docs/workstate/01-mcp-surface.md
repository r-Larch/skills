# 01 — The MCP surface

> Target revision: **2026-07-28** (current). SDK: `ModelContextProtocol.AspNetCore` **2.2.0**
> (2026‑08‑13), which targets net8/9/10 and states stable alignment with 2026‑07‑28 plus down-level
> negotiation to 2025‑11‑25.
> Sibling packages: `ModelContextProtocol.Core`, `ModelContextProtocol.Extensions.Tasks`,
> `ModelContextProtocol.Extensions.Apps` — all 2.2.0.

---

## 1. What changed in 2026-07-28 that shapes this design

| Change | Consequence here |
|---|---|
| **Sessions removed.** No `initialize`, no `Mcp-Session-Id`. Servers needing cross-call state use *"explicit, server-minted handles passed as ordinary tool arguments"* | Perfect fit. Every call is keyed by `workspace`/`ref`. Nothing is implicit. No session affinity, so the service scales horizontally behind Traefik with zero sticky routing. |
| **`tools/list` MUST NOT vary per-connection, but MAY vary by the authorization presented** | The lever for role enforcement: an orchestrator token and a worker token see **different tool sets** (§6). Note the flip side — we **cannot** hide tools based on run state, so state enforcement must be errors, not absence. |
| **Roots, Sampling and Logging are DEPRECATED** | Do not design around any of them. Pass the repo root as a tool argument / server config. Log to stderr + OpenTelemetry. |
| **Tasks moved to an official extension** (`io.modelcontextprotocol/tasks`, poll via `tasks/get`) | Not needed in v1 — every operation here is a sub-50 ms database write. Revisit only for `work_import` over a large repo. |
| **MRTR replaces server-initiated requests.** A server returns `resultType: "input_required"` with `inputRequests`, the client retries with `inputResponses` + `requestState` | Attractive for confirming destructive ops, but client support is uneven. **Use a server-minted confirmation handle instead** (the spec's own "Stateful Tools" pattern): a destructive call returns a `confirm_token`, the second call carries it. Works on every client. |
| **`CacheableResult`: `ttlMs` + `cacheScope` required on all list results** | `tools/list`/`prompts/list` → `ttlMs` ~300 000, `cacheScope: "public"`. `resources/read` on live state → **short `ttlMs` (≤5 000) and `cacheScope: "private"`**; caching work state aggressively is how a client shows a stale board. |
| **`subscriptions/listen`** replaces the SSE GET endpoint and `resources/subscribe` | Used for `notifications/resources/updated` when an item changes. Optional for v1 — the web UI gets its own SSE stream from the App API, which is not MCP. |
| **`structuredContent` may be any JSON value**; `outputSchema` any JSON Schema 2020-12 | Every read tool gets a real `outputSchema`. |
| **Deterministic `tools/list` ordering SHOULD** | Fixed declaration order; improves the client's prompt-cache hit rate — which matters, because these tools are in context for the whole run. |

---

## 2. The output contract — settled by measurement, 2026-09-18

> **This section was rewritten after the probe.** The original design — compact text in `content`,
> typed JSON in `structuredContent`, deliberately not duplicated — **does not work in Claude Code**,
> and would have made the compact rendering invisible. `mcp/PROBE.md` §6 has the evidence. The
> superseded version is not reproduced here; it was wrong and being able to grep it would only
> mislead.

**Every read tool returns a text block and nothing else.**

```csharp
new CallToolResult { Content = [ new TextContentBlock { Text = <the compact rendering> } ] }
```

No `structuredContent`. No `OutputSchemaType`. Three measured facts force this:

1. **`structuredContent` replaces `content`; it does not accompany it.** A result carrying both
   delivered only the JSON. A result carrying text and *no* structured half delivered the text
   verbatim — so this is a specific rule, not general suppression.
2. **"Both" is therefore not on the menu.** The back-compat duplication the spec *SHOULD*s was
   measured too: the model received one copy, from the JSON half. **Choosing to send
   `structuredContent` is choosing to lose the rendered text.**
3. **JSON destroys the layout even when it carries every byte.** The same rendering shipped inside a
   JSON string arrived as literal `\n` escapes on one line. Over a 40-line board that is 40 escapes
   the model must mentally re-lay-out, and the column alignment and separators — which are the
   information — are gone at the point of reading. The observer, asked which shape it would want as
   the consumer, chose text without hesitation and said why: *"A arrived as real lines I read
   directly."*

`outputSchema` is **not visible to the model at all** (measured), so it buys nothing for the audience
this server is built for.

**Nothing is lost by dropping the JSON.** The typed channel for the dashboard is the App API, which
is REST and has always been where the web UI reads from. MCP is the agent channel; the App API is the
machine channel. Two audiences, two transports, no compromise between them.

*If* a programmatic MCP consumer ever appears, the escape hatch is a `format: "json"` argument on the
affected tool — one flag, decided then, on evidence. Not built now.

### The compact rendering

Fixed-width, one line per item, ~14 tokens each:

```
2.3  task   done      EntityProjector                    ✓12/12  1b6034b5
2.4  task   doing 43m Run service + Quartz               ⚠ no verify recorded
2.5  task   blocked   Admin API                          ← 2.4
2.6  task   open      Economics gate test                rev-req
F-11 find   open      BLOCKER ContentHash written early  → 2.8
A6   assum  open      Docker can run pgvector            revisit: 1.1
```

Every line begins with the **`ref`** — the handle for every subsequent call. Ids never appear; `ref`
is the only address an agent needs, and there is no second channel to put ids in.

**The layout is load-bearing, and it survives.** Real line breaks, column alignment and separator
lines all reached the model intact (measured). That is the whole argument for a text-only contract:
a status board is scannable because of its shape, and shape is the first thing JSON destroys.

### The attention block

Appended to **every** result, read or write, capped at 5 lines and suppressed entirely when empty:

```
--- attention (3) ---
⚠ 2.4 in_progress 43m, claim expired 13m ago — release or heartbeat
⚠ A3 past revisit_when (task 3.1 is done) — work_assume resolve
⚠ 2 notes addressed to 3.1 unconsumed — they render into its brief
```

This is the nag channel. It costs ~30 tokens, rides on calls the agent is already making, and is the
mechanism that replaces 1,241 hand-written ⚠ markers.

### Resource links instead of payloads

Anything large comes back as a `resource_link`, never inline:

```json
{ "type": "resource_link", "uri": "work://item/gaia.entity-store.2.3/brief",
  "name": "brief 2.3", "mimeType": "text/markdown" }
```

The client fetches it only if it needs it. This is how `work_get` on a task with a 400-line brief
costs 40 tokens instead of 6,000.

**Measured caveat:** a resource link arrives as a *labelled bare URI in the text*, not as a followable
affordance — `[Resource link: probe_fixed] work://probe/fixed`. Nothing is inlined, and the model
retrieves it only if it already knows `ReadMcpResourceTool` exists. So the saving is real **but not
self-executing**: every tool that returns a link must name the follow-up call in its own description,
or the link is a dead end. This is the same rule as §9 — the doctrine has to travel with the tool.

---

## 3. Tools

20 tools. Deterministic order. `readOnlyHint` on all of §3.1; `destructiveHint` only where marked.
Names are `work_*`-prefixed so they survive aggregation across servers.

### 3.1 Read — orient, then query

| Tool | Arguments | Returns | Replaces |
|---|---|---|---|
| **`work_brief`** | `scope` (workspace or item ref), `for` = `resume\|handoff\|phase` | The recovery payload: goal chain, current position, next action, branch/HEAD, mode, binding constraints in scope, open assumptions, unconsumed notes, deferred follow-ups, last verification. | The `LEDGER.md` header block — **generated, therefore never stale**. The first call of every session. |
| **`work_next`** | `scope`, `limit`, `kind?` | The ready frontier: items whose blocking dependencies are all `done`, in `seq` order, with their `verify_cmd` and risk flags. | "read PLAN.md and work out what's next" |
| **`work_tree`** | `scope`, `depth`, `status in[]`, `include_counts` | Indented tree with per-node rollups (`open/doing/blocked/review/done`). | `PLAN.md` § Phases, and the checkbox list that never gets checked |
| **`work_get`** | `ref[]` (batch), `include` = `body\|criteria\|deps\|attempts\|findings\|notes` | Full item(s). Body returned as `resource_link` unless `include: body` is asked for. | reading a section of the ledger |
| **`work_search`** | `q`, `scope?`, `in` = `items\|blocks\|events\|notes\|reports`, `limit` | FTS + trigram hits with `ref` + snippet. | `Grep` over the run directory — which is what agents do today |
| **`work_threads`** | `scope`, `include` = `assumptions\|findings\|followups\|notes\|stale\|deps` | **Everything unclosed that is not a task.** The single query the user asked for. | `grep -c '⚠'` |
| **`work_audit`** | `scope`, `checks[]` | Mechanical integrity report (§5). | the cheap 60% of a 60–150k-token plan audit |
| **`work_history`** | `ref?`, `since_seq?`, `limit` | Append-only event stream, cursor-paged. | ledger § Exceptions, and "what happened while I was compacted" |

### 3.2 Write — structure

| Tool | Notes |
|---|---|
| **`work_create`** | **Batch**: takes an array. Decomposition creates 6–20 items in one call. Refuses a `task` without `verify_cmd` and without ≥1 done-criterion — the skill's *"a brief that omits done-criteria or a verification command is a defect"*, made unwritable. Accepts an agent-chosen `ref` (`2.1b`, `3.R1` — the corpus uses these constantly) or derives one; `UNIQUE(ref_scope_id, ref)` makes duplicate task numbers impossible **within a run**, while leaving parallel runs free to use the same numbering. |
| **`work_update`** | Field-level patch of `title`/`intent`/`verify_cmd`/`risk`/`criteria`/`priority`. Takes `expected_revision`; a mismatch returns the current revision and what changed. |
| **`work_move`** | Reparent / reorder. Separate from `update` because it is the risky one. Fractional `seq` means inserting `2.1b` between `2.1` and `2.2` rewrites **one row**. |
| **`work_link`** | `blocks`, `supersedes`, `duplicates`, `derived_from`, `verifies`. Cycle-checked on write. `supersedes` sets the target to `superseded` — which is the skill's *"a superseded task is deleted, not annotated"*, enforced. |
| **`work_status`** | The guarded transition (§4). `destructiveHint: true` for `cancel`. |

### 3.3 Write — content

| Tool | Notes |
|---|---|
| **`work_patch`** | Block-addressed document patching (§7). Batch array of ops: `set`, `insert_after`, `insert_before`, `append`, `delete`, `move`. Addresses blocks by **stable id**, so there is no anchor to reproduce and no uniqueness collision. `expected_revision` per block. |

### 3.4 Write — evidence and journal

| Tool | Notes |
|---|---|
| **`work_report`** | Submits the worker report *as its existing schema*: `status`, `files[]`, `verify`, `red_check`, `deviations`, `assumptions[]`, `risks[]`, `not_done`. Creates the attempt + report, and **auto-creates `assumption` rows from `assumptions[]`** — so the field the skill calls *"the highest-value line in the format"* stops being prose. |
| **`work_verify`** | Records `command`, **`result_line`** (the actual `12 passed` line, not an exit code), `passed`, `counts`, `run_by` (`worker\|orchestrator`), optional `red_check {reverted_what, failed_count}`, `samples[]` for measurements (the skill requires ≥3 with median and range). |
| **`work_note`** | `kind` = `carry_forward \| do_not_rederive \| correction \| incident \| environment \| decision`; optional **`to`** = a future item ref or a selector (`phase:3`, `kind:task under 4`). Unconsumed notes addressed to an item are injected into its brief and marked consumed. |
| **`work_assume`** | Create or resolve. Required on create: `statement`, `evidence_level`, `reversal_cost`, `revisit_when`. Resolve: `confirmed \| disproven \| obsolete` + evidence. Past `revisit_when` → it appears in `attention` until resolved. |
| **`work_constrain`** | Create or retire a `constraint_rule` — the writer the BINDING block never had, without which the block is import-only and a project started in the store has an empty one forever. Create: `kind` (`red_line\|stack_fact\|command\|domain_rule\|generated_code`), `text`, `route`, `scope` (an item ref; omitted, it binds the workspace), `covers_risk[]`, `severity`, `from`. A `red_line` with no `route` is refused — doc 02 §8. Retire: `ref` + `reason`, which sets `active = false` and leaves the row, because the record is the point. |
| **`work_finding`** | Batch. From a review, gate or audit. Each becomes an item of kind `finding` with `severity` (`blocker\|minor`), a `target` item, and a `source` (`review\|gate\|plan_audit\|self`). A `blocker` linked to a phase **blocks that phase's `done` transition**. |

### 3.5 Admin

| Tool | Notes |
|---|---|
| **`work_init`** | Create/configure a workspace: repo root, default branch, item-kind ladder, verification command catalogue. |
| **`work_import`** | Parse an existing `.claude/orchestration/<slug>/` directory into items + docs, best-effort, preserving every original line verbatim as blocks. 181 files exist; this is how they arrive. |

---

## 4. The `work_status` guard — where "forced to close" lives

```
open → ready → in_progress → review → done
                    ↓            ↓
                 blocked      rework → in_progress
                    ↓
       cancelled | superseded | deferred(→ creates an item elsewhere)
```

`done` is refused unless **all** hold:

1. every `done_criterion` is `checked`;
2. ≥1 `verification` row with `passed = true` whose `command` matches the item's `verify_cmd`
   **and `run_by = orchestrator`** — the skill's *"always, no exceptions: run the command yourself"*,
   as a constraint;
3. no child item is open;
4. no open `finding` with `severity = blocker` targets it;
5. for a task: ≥1 artifact of kind `commit` — *"every accepted task ends in a commit"*.

A refusal is `isError: true` with self-correcting text, because the spec says tool execution errors
are what models recover from:

```
REFUSED: 2.4 → done. 3 conditions unmet.
  · criterion c2 "DeletesSkipped surfaced on DatasetSourceRun" unchecked
      → work_update(ref:"2.4", criteria:[{id:"c2", checked:true, evidence:"…"}])
  · no orchestrator verification (worker-reported only)
      → work_verify(ref:"2.4", command:"dotnet test --filter Runs", result_line:"…", run_by:"orchestrator")
  · finding F-07 (blocker) open against 2.4
      → work_status(ref:"F-07", to:"done") or work_status(ref:"F-07", to:"deferred", target:"backlog", reason:"…")
```

Each rule is individually overridable with `force: true` + `reason`, which writes a
`forced_transition` event that is **permanently visible in `work_brief` and `work_audit`**. Escape
hatches that are silent get used; escape hatches that leave a mark do not.

`in_progress` requires a claim (`agent`, TTL, heartbeat). A second claim on the same item, or on a
sibling within a `serialized` scope, is refused — which is *"writing agents run one at a time, never
two in parallel"* enforced instead of requested.

---

## 5. `work_audit` — the mechanical half of the plan audit

The plan-audit brief lists 8 checks. Five of them are queries; three need an agent. Moving the five
is worth 60–150k tokens per audit, and lets the agent spend its whole budget on the one that pays.

| Audit check | Mechanical? |
|---|---|
| #1 *Could each remaining `Verify:` command fail?* | **Partly.** The server flags any `verify_cmd` that has **never been recorded red** anywhere in the workspace — the exact "filter matches only pre-existing passing tests" defect. Actually *running* them stays the agent's job. |
| #2 Dependencies exist or are commissioned | **Yes** — FK + "blocked by an item with no owner and no `seq` before mine". |
| #3 Every task number defined exactly once | **Yes** — a unique constraint; unviolatable. |
| #4 Load-bearing facts still true | No — needs an agent. |
| #5 Documents contradict each other | **Mostly gone** — there is one copy. Flags `superseded` items still referenced by open ones. |
| #6 Do the constraints still cover what upcoming tasks can destroy? | **Partly** — flags upcoming items whose `risk.blast_radius` tags have no matching active `constraint_rule` in scope. |
| #7 Ordering / contract inversions | **Sequence: yes** (dep graph vs `seq`). Contract inversion: no. |
| #8 **Shape** | No — and this is the finding that pays for the audit. Leave the whole budget here. |

Plus checks no file-based audit can do: items `done` without verification; forced transitions;
expired claims; findings deferred to nowhere; assumptions past `revisit_when`; notes never consumed;
docs whose blocks were never read by the agent that closed the item they belong to.

---

## 6. Authorization, roles and scoped tool lists

Per-request bearer token; `tools/list` varies by the token's grants. Three profiles:

| Profile | Tools | Scope | Rationale |
|---|---|---|---|
| **worker** | `work_get`, `work_brief`, `work_search`, `work_report`, `work_verify` (`run_by` forced to `worker`), `work_note` | one item subtree | A worker **structurally cannot** close its own task, create tasks, or record an orchestrator verification. The brief no longer has to ask. |
| **orchestrator** | everything except `work_init`, and no cross-run links | **its own run subtree** | Judges, transitions, commits. Many run in parallel; each sees its own run by default. |
| **controller** | everything, every project | global | Owns the queue tier, and is the only role that may create `blocks` edges **across runs** — the integration ordering in `controller` §2. |

Tokens are minted per agent with a scope and a TTL, so a nested `claude --bg` launch carries a token
that only reaches its own unit. The `actor` on every event comes from the token, never from a
self-declared argument — which is what makes the web UI's audit trail trustworthy when six agents are
writing at once.

**Implementation — measured end-to-end, 2026-09-18.** `AddAuthorizationFilters()` makes the SDK honour
`[Authorize]` on tool methods, so each profile is an ASP.NET Core policy and each tool carries an
attribute. **That alone narrows `tools/list`**: a reader token saw 2 tools, a writer token saw 3, and
the reader could not even *attempt* the writer-only call — the client refused locally with *"No such
tool available"*, so no request reached the server.

⛔ **`WithListToolsHandler` is therefore NOT needed.** The earlier plan to wire it as a second
mechanism was insurance against a narrowing that turns out to be automatic. **U3 is simpler than
designed.** Lists are per-registration, so the cross-token cache concern does not arise either.

*Consequence worth noting:* because the tool is hidden rather than refused, **a worker never sees an
error explaining what it may not do.** The role boundary must therefore be stated in
`ServerInstructions` — *"you are a worker; closing a task is the orchestrator's call"* — or the agent
will hunt for a tool that, from where it stands, simply does not exist.

> `nested-claude` §"every prohibition needs a route" applies directly: a scoped tool list is a
> prohibition, so each profile's tool set must include the **route** for everything the brief asks
> for. A worker that must report an assumption needs `work_note`; take it away and it parks.

---

## 7. `work_patch` — beating `Edit`

Documents (`CONVENTIONS`, `SPEC` pointer, `HANDOFF`, a brief body, an analysis note) are stored as an
**ordered list of blocks with stable ids**, not as a blob.

A read renders ids inline:

```
[b3] ## Safety rules — binding on every task
[b4] - Do not commit or push. Leave changes in the working tree.
[b5] - Do not delete any file not named in your brief's scope.
```

A patch addresses them:

```json
{ "doc": "gaia.entity-store#conventions", "ops": [
  { "op": "set",          "block": "b5", "rev": 2, "body": "- Do not delete any file not named in scope. Report and stop." },
  { "op": "insert_after", "block": "b5", "body": "- Never run an unscoped DELETE against the dev database." },
  { "op": "delete",       "block": "b9", "rev": 1 }
]}
```

Against `Edit`, this is better on every axis:

| | `Edit` | `work_patch` |
|---|---|---|
| Find the target | grep first, then reproduce the anchor byte-exact | the id was in the last read |
| Ambiguity | fails when the anchor recurs — i.e. in tables and lists | impossible |
| Lost updates | second writer silently wins | `rev` mismatch → refused, with the current value |
| Cost of a 1-line change in a 6,000-line doc | anchor text + surrounding context in the prompt | ~20 tokens |
| Line endings | a read-then-write silently rewrites them (this repo has the commit) | text is a column; no file, no CRLF |
| Undo | none | every block revision is an event |

`append` (`{op:"append", body}`) stays as cheap as `cat >>`, which is why it is the write the corpus
is 90% made of.

---

## 8. Resources

URI scheme `work://`. Read-only projections, `cacheScope: "private"`, short `ttlMs`.

| URI / template | Contents |
|---|---|
| `work://workspace/{slug}` | index: goal, tree summary, attention |
| `work://workspace/{slug}/brief` | the recovery payload as markdown — **attachable in Claude Code with `@`** |
| `work://workspace/{slug}/conventions` | the binding constraints, rendered |
| `work://item/{ref}` | one item, rendered |
| `work://item/{ref}/brief` | the fully rendered worker brief (see §9) |
| `work://doc/{id}` | a document with block ids inline |
| `work://workspace/{slug}/handoff` | the "competent stranger could continue from this alone" document |

**Measured, 2026-09-18 — what actually works:**

- **Fixed URIs are listed and readable.** ✅ Keep every fixed projection in the table above.
- **Templates are readable but *undiscoverable*.** `resources/templates/list` produced nothing the
  client showed; `work://probe/alpha` nonetheless read correctly when the URI was known. So a
  templated URI is only reachable if the model was **handed the exact URI** — which a tool result or
  `ServerInstructions` can do, and a picker cannot. Every tool that references an item must emit its
  full `work://item/{ref}` URI rather than assume it can be composed.
- **`completions/complete` has no client mechanism at all.** ⛔ **The completion plan is dropped**, and
  `WithCompleteHandler` is not wired. The "live item picker for free" was wishful; there is no picker.
- **Exact URIs win over templates.** Reading `work://probe/fixed` returned the *fixed* resource, not
  the template match. That settles the `work://workspace/{slug}` vs sub-path overlap: **register the
  exact URIs, and let the template catch only what is left.**

`subscriptions/listen` with `resourceSubscriptions` emits `notifications/resources/updated` when an
item changes. Unproven in this client and **not needed** — the web UI uses its own SSE stream from
the App API. Deferred, not designed around.

---

## 9. The doctrine channel — ⛔ NOT prompts

> **Rewritten after the probe.** MCP prompts are **invisible in Claude Code**: absent from the tool
> list, absent from the skills listing, not invokable (`Unknown skill`). Measured, not inferred.
> `docs/workstate/04` §5 predicted this was the least certain channel. It was right.

The *content* below is still exactly right — a brief must be server-rendered with live state
injected, or the orchestrator types it and forgets half. Only the **delivery mechanism** changes:

| Was going to be a prompt | Becomes |
|---|---|
| `worker_brief` | **a tool** — `work_brief(ref, for: "worker")`. It already had to exist; it absorbs this. |
| `review_brief`, `gate_brief`, `plan_audit_brief`, `handoff`, `retro` | `for:` variants of the **same** `work_brief` tool. One tool, one description, six renderings. |
| `decompose` | text in `ServerInstructions` + the `work_create` description, since it is a *rule* ("a `Verify:` must be able to fail"), not a document. |

This is a **better** outcome than the prompt design, and worth saying plainly rather than filing as a
regression. A prompt is something a human chooses from a menu; a brief is something an agent needs
mid-task. Making it a tool call puts it where the work is, it composes with `work_next`, and it costs
one fewer primitive to explain. The probe cost half an hour and removed a feature.

**The doctrine now travels in three places, all measured to work:** `ServerInstructions` (the loop and
the closure contract), tool descriptions (what to call next), and `work_brief`'s rendered output (the
per-item detail). `orchestrate/references/briefs.md`'s 311 lines land in the third.

### What each `work_brief` rendering must contain

`work_brief(ref, for: …)` — one tool, one description, six renderings. The content below is unchanged
from the prompt design; only the delivery moved.

| `for:` | Renders |
|---|---|
| `resume` *(default)* | Goal chain · current position · **the literal next action** · branch/HEAD · binding constraints in scope · open assumptions · unconsumed notes · deferred follow-ups · last verification. The recovery payload, generated so it cannot be stale. |
| `worker` | Context (goal chain + what previous tasks delivered, from the DB) · binding constraints **in scope** · **every unconsumed note addressed to this item** · in/out of scope · done-criteria · `verify_cmd` · the red-check demand · the report format. **Marks the notes consumed.** |
| `review` | Scoped diff range, done-criteria, goal paragraph, the 5-finding cap, the bar for a finding. |
| `gate` | The phase's tasks, **the recorded seams** (`work_link` edges crossing task boundaries — computed, not remembered), the seam classes. |
| `audit` | Pre-loaded with `work_audit`'s mechanical results marked *established, do not re-verify*, so the agent's budget goes to shape. |
| `handoff` | The stranger-continues document. |
| `retro` | Built from the events, verdicts and forced transitions already stored. |

What stays in `SKILL.md` is the judgment: the cost table, the risk dial, red-checks, verification
doctrine, escalation triggers, the critical-thinking ceiling. That is the half worth a model's
attention; the templates never were.

---

## 10. Deliberate non-use

| Feature | Why not |
|---|---|
| **Sampling** | Deprecated in 2026-07-28. |
| **Roots** | Deprecated. Repo root is workspace config. |
| **Logging (`logging/setLevel`)** | Removed/deprecated. stderr + OpenTelemetry. |
| **Tasks extension** | Every operation is a fast DB write. Revisit for `work_import` only. |
| **MCP Apps** | Interesting later (an inline board widget in the client); the standalone SPA comes first. |
| **MRTR / elicitation** | Confirmation handled with a server-minted `confirm_token`, which works on every client. Revisit when client support is uniform. |
| **Prompts** | **Measured invisible in Claude Code** (§9). Not registered at all. |
| **`completions/complete` / `WithCompleteHandler`** | **Measured: no client mechanism exists** (§8). Not wired. |
| **`structuredContent` / `OutputSchemaType`** | **Measured: replaces the text block, and the schema never reaches the model** (§2). Not used on read tools. |
| **`WithListToolsHandler`** | **Measured unnecessary — `[Authorize]` narrows the list by itself** (§6). |

---

## 11. Verified SDK surface — probed 2026-09-18

Read from the actual assemblies (`/rlarch:dotnet-reflect` over `ModelContextProtocol` /
`.Core` / `.AspNetCore` **2.2.0**). Everything below is metadata, not documentation. This section
supersedes every "to verify" note earlier in this document.

### Hosting

| Symbol | Signature | Note |
|---|---|---|
| `MapMcp` | `static IEndpointConventionBuilder MapMcp(this IEndpointRouteBuilder, string pattern)` | `Microsoft.AspNetCore.Builder.McpEndpointRouteBuilderExtensions`. Returns a convention builder, so `.RequireAuthorization("agent-token")` chains. |
| `WithHttpTransport` | `(this IMcpServerBuilder, Action<HttpServerTransportOptions>?)` | |
| **`AddAuthorizationFilters`** | `(this IMcpServerBuilder)` | *"should **always** be called when using ASP.NET Core integration"*. Enables `[Authorize]` **on tools, prompts and resources**. This is the mechanism for §6 — better than anything hand-rolled. |
| `HttpServerSessionMode` | `Stateless` \| `Stateful` \| `StatefulForInitializeClients` | **`Stateless` is the default as of the 2026-07-28 revision**; every stateful member is `[Obsolete]` and documented as a back-compat escape hatch for ≤2025-11-25 clients (SEP-2567). Our design is stateless anyway. |
| `EnableLegacySse` | `[Obsolete]` | Do not use. |

### Primitives

| Symbol | Note |
|---|---|
| `WithTools<T>` / `WithToolsFromAssembly` / `WithPrompts*` / `WithResources*` | All present; assembly scanning keyed on `[McpServerToolType]` / `[McpServerPromptType]` / `[McpServerResourceType]`. |
| `[McpServerTool]` | `Name`, `Title`, **`ReadOnly`**, **`Destructive`**, **`Idempotent`**, **`OpenWorld`**, **`UseStructuredContent`**, **`OutputSchemaType`**, `IconSource`. Every annotation doc 01 assumed is a real attribute property. |
| `[McpServerResource]` | `UriTemplate`, `Name`, `Title`, `MimeType`, `IconSource` — RFC 6570 templates are declarative. |
| `[McpServerPrompt]` | `Name`, `Title`, `IconSource`. |
| `CallToolResult` | `IList<ContentBlock> Content`, `JsonElement? StructuredContent`, `bool? IsError`. |
| `ResourceLinkBlock` | `Uri`, `Name`, `Title`, `Description`, `MimeType`, `Size`, `Icons`. Exists — the resource-link strategy is implementable as written. |

**The output contract is directly supported.** A tool method may return `CallToolResult` itself,
which gives exact control over `Content` (our compact text) and `StructuredContent` (the JSON),
**without** the SDK's automatic duplication — while `OutputSchemaType = typeof(WorkTreeResult)` still
publishes a real `outputSchema`. That is doc §2's "both, not duplicated" decision, implementable
with no fighting the framework.

### Cross-cutting

| Symbol | Use here |
|---|---|
| `WithRequestFilters` / `WithMessageFilters` | The **attention block**, actor resolution from the token, and OpenTelemetry spans — all as filters, so no tool body repeats them. |
| ~~`WithListToolsHandler`~~ | **Exists, NOT used.** `[Authorize]` narrows `tools/list` unaided (measured, §6). |
| ~~`WithCompleteHandler`~~ | **Exists, NOT used.** The client offers no completion mechanism at all (measured, §8). |
| `WithSubscriptionsListenHandler` | SEP-2575 subscription stream, if we later push `resources/updated` to a watching controller. |
| `WithSetLoggingLevelHandler` | `[Obsolete]` — confirms the Logging deprecation. Do not wire it. |
| `AddMcp()` on `AuthenticationBuilder` | OAuth protected-resource-metadata handler. **Not needed** for a static bearer token; keep in reserve if this is ever exposed beyond localhost. |

### What this changes in the plan

- Task 0.1 is **done**; the design needed no correction.
- §6 gets simpler and stronger: policies + `[Authorize]` + a filtered list handler.
- The stateless decision is the framework default, not a setting to fight.
- **Task 0.2 (what Claude Code actually surfaces of prompts / resources / completions) is still
  open**, and it is the one that decides whether §9's prompts carry weight in v1.
