# 02 — The Postgres data model

> PostgreSQL 17+. Extensions: `pg_trgm`, `btree_gin`. Snake case throughout (workspace convention —
> `UseSnakeCaseNamingConvention()`). Ids are **UUIDv7** minted app-side with .NET's
> `Guid.CreateVersion7()`, so primary keys sort by creation time and index locality is good.

Two structures carry everything:

1. **A containment tree** — one recursive `item` table covering *project → feature → plan → epic →
   phase → task → sub-task → finding → follow-up*, with `kind` as configurable data rather than as a
   table per level.
2. **A dependency DAG over it** — `blocks`, `supersedes`, `duplicates`, `derived_from`, `verifies`.

Everything else hangs off those: evidence, journal, documents, constraints, and the note outbox.

---

## 1. Enumerations

```sql
CREATE TYPE item_status AS ENUM (
  'open',        -- exists, not yet actionable or not yet picked up
  'ready',       -- all blocking deps satisfied; in the frontier
  'in_progress', -- claimed, lease active
  'blocked',     -- explicitly blocked (dep, escalation, or external)
  'review',      -- work complete, judgement pending
  'done',
  'cancelled',
  'superseded'   -- replaced by another item; set only via a `supersedes` link
);

CREATE TYPE item_resolution AS ENUM (   -- only meaningful when status = 'done'
  'accepted','reworked','replanned','escalated','skipped','wontfix','duplicate','obsolete'
);

CREATE TYPE link_type AS ENUM (
  'blocks',        -- from blocks to
  'supersedes',
  'duplicates',
  'derived_from',  -- rework/split lineage: 2.1b derived_from 2.1
  'verifies'       -- a test/finding that verifies a target
);

CREATE TYPE assumption_state AS ENUM ('open','confirmed','disproven','obsolete');
CREATE TYPE evidence_level  AS ENUM ('asserted','reasoned','code_read','probed','measured');
CREATE TYPE reversal_cost   AS ENUM ('trivial','low','medium','high','irreversible');

CREATE TYPE note_kind AS ENUM (
  'carry_forward','do_not_rederive','correction','incident','environment','decision','observation'
);

CREATE TYPE constraint_kind AS ENUM ('red_line','stack_fact','command','domain_rule','generated_code');
CREATE TYPE actor_role      AS ENUM ('controller','orchestrator','worker','reviewer','human','system');
CREATE TYPE verify_runner   AS ENUM ('worker','orchestrator','ci','human');
```

---

## 2. One global server; a project per repo; parallel runs inside it

**Settled (2026-09-18).** There is **one WorkState deployment**, single-user. A **project** is an
item, one per repo. Runs live under projects, and **many orchestrators run in parallel**, each owning
its own run subtree.

So `workspace` is a thin singleton holding server config, not a tenancy boundary — the tree is the
only structure:

```sql
CREATE TABLE workspace (                          -- one row today; the seam if multi-user ever lands
  id           uuid PRIMARY KEY,
  slug         text NOT NULL UNIQUE DEFAULT 'default',
  settings     jsonb NOT NULL DEFAULT '{}',       -- claim_ttl, ref policy, render widths
  created_at   timestamptz NOT NULL DEFAULT now()
);
```

Repo facts move onto the project item, because they belong to the repo and not to the server:

```sql
CREATE TABLE project_config (
  item_id        uuid PRIMARY KEY REFERENCES item ON DELETE CASCADE,  -- kind = 'project'
  repo_root      text NOT NULL,                  -- P:\Projects\LarchSys\Gaia
  default_branch text NOT NULL DEFAULT 'master',
  verify_catalog jsonb NOT NULL DEFAULT '{}'     -- build / typecheck / filtered / full
);
```

### Ref scoping — short handles that cannot collide across parallel runs

Agents type `2.3` all day; they will not type `gaia/entity-store/2.3`. But two runs in one project
both have a `2.3`. So **`ref` is unique within its nearest ref-scoping ancestor**, not globally:

- kinds marked `scopes_refs` (default: `project` and `run`) open a new ref namespace;
- `item.ref_scope_id` denormalises the nearest such ancestor, maintained by the same trigger as
  `ancestors`;
- the handle an agent passes is `"2.3"` when the call carries a run context (from its token or an
  explicit `run` argument), or the qualified `"entity-store/2.3"` from anywhere.

`UNIQUE (ref_scope_id, ref)` still makes plan-audit check #3 unviolatable — now per run, which is the
scope in which duplicate task numbers actually cause harm.

### Kinds are data

`projects > features > plans > epics > tasks > sub-tasks > etc.` is configuration, not a migration.
`allowed_parent_kinds` is what stops a "phase" being filed under a "task".

```sql
CREATE TABLE item_kind (
  key                 text PRIMARY KEY,           -- 'project','run','phase','task','finding', …
  label               text NOT NULL,
  rank                int  NOT NULL,              -- coarse depth hint, for rendering
  allowed_parent_kinds text[] NOT NULL DEFAULT '{}',
  scopes_refs         boolean NOT NULL DEFAULT false,  -- true for 'project','run'
  requires_verify     boolean NOT NULL DEFAULT false,  -- true for 'task'
  requires_criteria   boolean NOT NULL DEFAULT false,
  requires_commit     boolean NOT NULL DEFAULT false,
  is_gateable         boolean NOT NULL DEFAULT false   -- true for 'phase'
);
```

Default ladder: `project → feature → run → phase → task → subtask`, plus the
attach-anywhere kinds `finding`, `followup`, `question`, `incident`, `spike`.

### Parallel orchestrators — what actually has to serialize

Two runs against **different worktrees** may write at the same time; two runs against the **same**
working tree may not. That is `nested-claude` §7 exactly (*"a `--bg` agent cannot edit your checkout
at all"*), so the lock is keyed on the tree, not on the run:

```sql
CREATE TABLE run_config (
  item_id          uuid PRIMARY KEY REFERENCES item ON DELETE CASCADE,  -- kind = 'run'
  project_item_id  uuid NOT NULL REFERENCES item,
  branch           text,
  worktree_path    text,
  serialization_key text NOT NULL,   -- defaults to worktree_path, else repo_root — the write lock
  mode             text NOT NULL DEFAULT 'full',   -- full | light
  owner_agent      text,                            -- the orchestrator that owns this run
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX run_serialization_idx ON run_config (serialization_key);
```

Consequences, all of them things the corpus had to enforce by hand:

- An orchestrator's token is scoped to its **run subtree**: `work_next`/`work_brief` default to its
  own work, and it cannot transition another run's items.
- Write claims are unique per `serialization_key`, so *"never two writing agents in parallel"* holds
  within a tree and correctly **does not** block two runs in separate worktrees.
- A **controller** token spans projects, sees every run side by side, and is the only role that may
  create `blocks` edges **across runs** — which is the integration ordering `controller` §2 describes
  ("branch each unit off the previous result").

---

## 3. `item` — the one recursive table

```sql
CREATE TABLE item (
  id            uuid PRIMARY KEY,
  workspace_id  uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  parent_id     uuid REFERENCES item ON DELETE RESTRICT,
  kind          text NOT NULL,
  ref           text NOT NULL,             -- '2.1b', '3.R1', 'F-07'  — the agent-facing handle
  ref_scope_id  uuid REFERENCES item,      -- nearest ancestor with scopes_refs (project/run); self if it is one
  seq           numeric NOT NULL,          -- fractional order among siblings
  ancestors     uuid[] NOT NULL DEFAULT '{}',  -- root..parent, maintained by trigger
  depth         int GENERATED ALWAYS AS (coalesce(array_length(ancestors,1),0)) STORED,

  title         text NOT NULL,
  intent        text,                      -- the 1–2 sentence "why" that goes in the brief
  status        item_status NOT NULL DEFAULT 'open',
  resolution    item_resolution,
  priority      smallint NOT NULL DEFAULT 0,

  verify_cmd    text,                      -- the narrowest command that CAN FAIL for this item
  risk_tags     text[] NOT NULL DEFAULT '{}',  -- auth,tenancy,schema,migration,contract,identity,
                                               -- money,delete,equivalence,production
  review_required boolean GENERATED ALWAYS AS (risk_tags <> '{}') STORED,
  gate_criterion  text,                    -- phases only: what must be true to accept the phase

  severity      text,                      -- findings only: 'blocker' | 'minor'
  source        text,                      -- findings only: 'review'|'gate'|'plan_audit'|'self'

  body_doc_id   uuid,                      -- FK added after `doc` (the brief / spec section)
  revision      int NOT NULL DEFAULT 1,

  opened_at     timestamptz NOT NULL DEFAULT now(),
  started_at    timestamptz,
  closed_at     timestamptz,
  closed_by     text,

  search tsvector GENERATED ALWAYS AS (
      setweight(to_tsvector('simple', coalesce(ref,'')),   'A') ||
      setweight(to_tsvector('english', coalesce(title,'')), 'B') ||
      setweight(to_tsvector('english', coalesce(intent,'')),'C')) STORED,

  CONSTRAINT item_ref_unique      UNIQUE (ref_scope_id, ref),          -- audit check #3, unviolatable
                                                                       -- scoped per run, which is where
                                                                       -- a duplicate number does harm
  CONSTRAINT item_sibling_order   UNIQUE (parent_id, seq) DEFERRABLE,
  CONSTRAINT item_resolution_only_when_done
      CHECK ((status = 'done') = (resolution IS NOT NULL)),
  CONSTRAINT item_closed_at_set
      CHECK ((status IN ('done','cancelled','superseded')) = (closed_at IS NOT NULL))
);

CREATE INDEX item_subtree_idx   ON item USING gin (ancestors);
CREATE INDEX item_open_idx      ON item (workspace_id, status, seq)
       WHERE status IN ('open','ready','in_progress','blocked','review');
CREATE INDEX item_parent_seq_idx ON item (parent_id, seq);
CREATE INDEX item_search_idx    ON item USING gin (search);
CREATE INDEX item_ref_trgm_idx  ON item USING gin (ref gin_trgm_ops);
CREATE INDEX item_risk_idx      ON item USING gin (risk_tags);
```

**Notes on the choices that matter:**

- **`ancestors uuid[]` + GIN over `ltree`.** Refs like `2.1b` and `3.R1` are not valid ltree labels,
  and the array gives `WHERE ancestors @> ARRAY[$run]` for a whole subtree plus ordered ancestry for
  free. A trigger maintains it on insert and on reparent.
- **`seq numeric`, fractional.** Inserting `2.1b` between `2.1` and `2.2` writes **one row**
  (`seq = (a+b)/2`). The corpus does this constantly — `2.1b`, `1.3r`, `3.R1`, `4.6b`, `4.8b`,
  `3.R2b` — and in a markdown plan each one is a hand edit that risks renumbering everything below.
  A periodic rebalance job resets precision.
- **`review_required` is generated from `risk_tags`.** The skill's risk dial (*auth, tenancy,
  schema, migration, published contract, shared identity rule, equivalence claim, money*) becomes a
  tagged column, so "which upcoming work needs a review agent" is a query rather than a re-read.
- **`ON DELETE RESTRICT` on `parent_id`.** Nothing in this system deletes; it cancels or supersedes.

### Done-criteria as rows, not a checkbox list

This is where 17 of 25 runs lost the truth. Criteria are rows with evidence, and the `done` guard
reads them.

```sql
CREATE TABLE item_criterion (
  id          uuid PRIMARY KEY,
  item_id     uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  seq         int  NOT NULL,
  text        text NOT NULL,
  checked     boolean NOT NULL DEFAULT false,
  evidence    text,                              -- how it was confirmed
  evidence_verification_id uuid,                 -- FK to verification, added below
  checked_at  timestamptz,
  UNIQUE (item_id, seq)
);
CREATE INDEX item_criterion_open_idx ON item_criterion (item_id) WHERE NOT checked;
```

---

## 4. Dependencies — the DAG the user asked for

```sql
CREATE TABLE item_link (
  id           uuid PRIMARY KEY,
  workspace_id uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  from_item_id uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  to_item_id   uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  type         link_type NOT NULL,
  note         text,
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   text,
  CONSTRAINT item_link_unique UNIQUE (from_item_id, to_item_id, type),
  CONSTRAINT item_link_no_self CHECK (from_item_id <> to_item_id)
);
CREATE INDEX item_link_to_idx   ON item_link (to_item_id, type);
CREATE INDEX item_link_from_idx ON item_link (from_item_id, type);
```

Cycle detection runs on insert of a `blocks` edge with a recursive CTE; a cycle is refused with the
path in the error. `supersedes` sets the target's `status = 'superseded'` in the same transaction —
the skill's *"a superseded task is deleted, not annotated"*, enforced.

### The two queries the user named

**Remaining work / the ready frontier** — the single hottest query:

```sql
WITH scope AS (SELECT id FROM item WHERE id = $1 OR ancestors @> ARRAY[$1]::uuid[])
SELECT i.ref, i.kind, i.title, i.verify_cmd, i.risk_tags
FROM   item i
JOIN   scope s ON s.id = i.id
WHERE  i.status IN ('open','ready')
  AND  NOT EXISTS (                          -- nothing unfinished blocks it
        SELECT 1 FROM item_link l JOIN item b ON b.id = l.from_item_id
        WHERE l.to_item_id = i.id AND l.type = 'blocks'
          AND b.status NOT IN ('done','cancelled','superseded'))
  AND  NOT EXISTS (                          -- and it has no open children of its own
        SELECT 1 FROM item c WHERE c.parent_id = i.id
          AND c.status NOT IN ('done','cancelled','superseded'))
ORDER BY i.priority DESC, sort_path(i);   -- NOT i.seq. See the note below.
```

> **Corrected 2026-09-20, by finding #4 of the corpus import.** `seq` orders *siblings*, so every
> phase's first task has `seq = 1` and ordering a whole subtree by it is arbitrary between phases.
> Order by the **document path** — the `seq` of each ancestor, then own `seq` — which the subtree
> query already builds:
>
> ```sql
> SELECT coalesce(array_agg(a.seq ORDER BY u.ord), '{}'::numeric[]) || i.seq
> FROM   unnest(i.ancestors) WITH ORDINALITY AS u(id, ord)
> JOIN   item a ON a.id = u.id
> ```
>
> 53 tests passed over the wrong version because **fixtures are single-phase and this defect only
> exists across phases.** Real multi-phase data found it in one run.
```

**Unclosed dependencies** — edges pointing at work that will never arrive:

```sql
SELECT t.ref AS waiting, b.ref AS on_, b.status, b.seq < t.seq AS ordered_correctly
FROM   item_link l
JOIN   item t ON t.id = l.to_item_id
JOIN   item b ON b.id = l.from_item_id
WHERE  l.type = 'blocks'
  AND  t.status NOT IN ('done','cancelled','superseded')
  AND  (b.status IN ('cancelled','superseded')          -- blocked on something abandoned
        OR (b.status = 'open' AND b.parent_id IS NULL)  -- blocked on an uncommissioned stub
        OR b.seq > t.seq);                              -- ordering inversion (audit check #7)
```

---

## 5. Documents as blocks — the patch target

```sql
CREATE TABLE doc (
  id           uuid PRIMARY KEY,
  workspace_id uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  item_id      uuid REFERENCES item ON DELETE CASCADE,  -- null = workspace-level
  slug         text NOT NULL,        -- 'conventions','handoff','brief','analysis','spec-pointer'
  title        text NOT NULL,
  revision     int NOT NULL DEFAULT 1,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (workspace_id, item_id, slug)
);

CREATE TABLE doc_block (
  id           uuid PRIMARY KEY,
  doc_id       uuid NOT NULL REFERENCES doc ON DELETE CASCADE,
  seq          numeric NOT NULL,       -- fractional: insert_after writes ONE row
  short_id     text NOT NULL,          -- 'b7' — the stable handle shown inline in reads
  heading      text,                   -- null for a body-only block
  heading_level smallint,
  body         text NOT NULL DEFAULT '',
  revision     int NOT NULL DEFAULT 1,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  search tsvector GENERATED ALWAYS AS (
      setweight(to_tsvector('english', coalesce(heading,'')),'A') ||
      setweight(to_tsvector('english', body),'B')) STORED,
  UNIQUE (doc_id, short_id),
  UNIQUE (doc_id, seq) DEFERRABLE
);
CREATE INDEX doc_block_search_idx ON doc_block USING gin (search);

CREATE TABLE doc_block_revision (      -- undo, and "what did this say when 2.3 was briefed"
  block_id  uuid NOT NULL REFERENCES doc_block ON DELETE CASCADE,
  revision  int  NOT NULL,
  heading   text,
  body      text NOT NULL,
  at        timestamptz NOT NULL DEFAULT now(),
  actor     text,
  PRIMARY KEY (block_id, revision)
);
```

`item.body_doc_id` now gets its FK. A block-level revision history makes *"the brief said X when the
worker ran, and says Y now"* answerable — which is the question every post-mortem in the corpus
eventually asks and no markdown file can answer.

---

## 6. Evidence — attempts, reports, verifications

```sql
CREATE TABLE attempt (
  id          uuid PRIMARY KEY,
  item_id     uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  n           int  NOT NULL,                    -- 1, 2, 3 …
  agent_name  text,                             -- 'fix-B-07'
  agent_kind  text,                             -- 'general-purpose' | 'Explore' | 'nested-claude'
  session_id  text,                             -- the .jsonl transcript handle
  brief_doc_id uuid REFERENCES doc,             -- the brief AS ISSUED (immutable snapshot)
  started_at  timestamptz NOT NULL DEFAULT now(),
  ended_at    timestamptz,
  outcome     text,                             -- 'done'|'partial'|'blocked'|'parked'|'killed'
  UNIQUE (item_id, n)
);

CREATE TABLE report (                            -- the worker report format, as a schema
  id          uuid PRIMARY KEY,
  attempt_id  uuid NOT NULL UNIQUE REFERENCES attempt ON DELETE CASCADE,
  status      text NOT NULL,                     -- done | partial | blocked
  files       jsonb NOT NULL DEFAULT '[]',       -- [{path, change}]
  deviations  text,                              -- "the highest-value line in the format"
  risks       text,
  not_done    text,
  raw         text,                              -- verbatim, for the record
  at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE verification (
  id          uuid PRIMARY KEY,
  workspace_id uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  item_id     uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  attempt_id  uuid REFERENCES attempt ON DELETE SET NULL,
  command     text,              -- NULLABLE. Corrected 2026-09-20 by finding #1 of the import:
                                 -- the corpus records the RESULT far more often than the command
                                 -- ("35/35, build 0 errors"). A NOT NULL here would mean the store
                                 -- cannot hold most of the evidence that actually exists, and a
                                 -- work_verify demanding one would teach agents to skip recording.
  result_line text NOT NULL,        -- "Passed! 187/187" — NOT an exit code. Required.
  passed      boolean NOT NULL,
  counts      jsonb,                -- {passed:187, failed:0, warnings:21}
  samples     jsonb,                -- measurements: [n1,n2,n3] + {median, range}
  run_by      verify_runner NOT NULL,
  red_check   jsonb,                -- {reverted:"…", failed_count:3, variants:[…]}
  at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX verification_item_idx ON verification (item_id, at DESC);
CREATE INDEX verification_cmd_idx  ON verification (workspace_id, command, passed);
```

That last index answers **plan-audit check #1** directly: *has this `verify_cmd` ever been recorded
red anywhere?* A command that has only ever passed is the decorative filter the skill warns about —
`--filter ~DatasetRun` reporting `Passed! 8` against an endpoint nobody implemented — and the server
flags it without an agent.

```sql
ALTER TABLE item_criterion
  ADD CONSTRAINT item_criterion_evidence_fk
  FOREIGN KEY (evidence_verification_id) REFERENCES verification ON DELETE SET NULL;
```

### Artifacts

```sql
CREATE TABLE artifact (
  id        uuid PRIMARY KEY,
  item_id   uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  kind      text NOT NULL,     -- commit | branch | file | pr | url | log | measurement | worktree
  value     text NOT NULL,     -- sha, path, url
  meta      jsonb NOT NULL DEFAULT '{}',
  at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX artifact_item_idx ON artifact (item_id, kind);
```

---

## 7. Assumptions — a state machine, not a strikethrough

```sql
CREATE TABLE assumption (
  id             uuid PRIMARY KEY,
  workspace_id   uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  scope_item_id  uuid REFERENCES item ON DELETE CASCADE,   -- null = whole workspace
  ref_scope_id   uuid REFERENCES item,      -- the run/project this ref is numbered within
  ref            text NOT NULL,                            -- 'A6'
  statement      text NOT NULL,
  evidence       evidence_level NOT NULL,
  evidence_note  text,            -- Added 2026-09-20, finding #3: the corpus writes the PROVENANCE
                                  -- ("confirmed: the key is in configuration"), and the enum keeps
                                  -- only the level. The provenance is the part a later reader needs.
  reversal       reversal_cost,   -- NULLABLE. Finding #2: a RESOLVED assumption has no reversal
                                  -- cost - the corpus writes an em dash - and forcing a value here
                                  -- made the importer invent 'medium'.
  revisit_item_id uuid REFERENCES item,      -- "Revisit when: task 1.1"  — a real edge
  revisit_at     timestamptz,                -- or a date
  state          assumption_state NOT NULL DEFAULT 'open',
  resolution_note text,
  resolved_at    timestamptz,
  created_at     timestamptz NOT NULL DEFAULT now(),
  UNIQUE (ref_scope_id, ref),   -- 'A1' exists once PER RUN, not once globally. With many
                                -- orchestrators running at once, a global unique would make the
                                -- second run unable to open its own A1.
  CONSTRAINT assumption_resolved CHECK ((state = 'open') = (resolved_at IS NULL))
);
CREATE INDEX assumption_open_idx ON assumption (workspace_id) WHERE state = 'open';
```

`revisit_item_id` is the piece markdown cannot do: when that item reaches `done`, the assumption is
**automatically surfaced in `attention`** until someone resolves it. In the corpus, `A6` ("Docker
usable here") was listed as *revisit when: Task 1.1* — and whether anyone did is unknowable from the
file.

---

## 8. Constraints — `CONVENTIONS.md`, scoped and queryable

```sql
CREATE TABLE constraint_rule (
  id             uuid PRIMARY KEY,
  workspace_id   uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  scope_item_id  uuid REFERENCES item ON DELETE CASCADE,   -- null = workspace-wide
  kind           constraint_kind NOT NULL,
  text           text NOT NULL,
  route          text,            -- "no pipes — narrow with the tool's own --filter"
  covers_risk    text[] NOT NULL DEFAULT '{}',  -- matches item.risk_tags
  severity       text NOT NULL DEFAULT 'binding',
  active         boolean NOT NULL DEFAULT true,
  added_by_item_id uuid REFERENCES item,        -- which phase discovered it was needed
  created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX constraint_scope_idx ON constraint_rule (workspace_id, scope_item_id) WHERE active;
CREATE INDEX constraint_risk_idx  ON constraint_rule USING gin (covers_risk);
```

Two things this enables that a file cannot:

- **Scoped rendering.** A brief for item X gets the workspace rules plus every rule scoped to X's
  ancestors — no more *"repeating the same fifteen safety lines in twelve briefs"*, and no more
  briefs missing the rule that was added after they were written.
- **Coverage checking** (plan-audit check #6). `covers_risk` vs the `risk_tags` of *upcoming* items
  answers *"does `CONVENTIONS.md` still forbid everything the remaining tasks can destroy?"* as a set
  difference. The `route` column is `nested-claude`'s hardest-won lesson made structural: a
  prohibition row without a route is itself a flagged defect, because *"a rule that demands a
  capability the agent does not have is a trap you set yourself."*

---

## 9. Notes — the outbox addressed to a future brief

The 36 *"do not re-derive"* lines in the corpus are knowledge with a named future consumer, stored in
a document and delivered by someone remembering to re-read it. Here they are addressed mail.

```sql
CREATE TABLE note (
  id            uuid PRIMARY KEY,
  workspace_id  uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  from_item_id  uuid REFERENCES item ON DELETE SET NULL,
  kind          note_kind NOT NULL,
  body          text NOT NULL,

  to_item_id    uuid REFERENCES item ON DELETE CASCADE,   -- deliver to this item's brief
  to_selector   jsonb,          -- or a rule: {"under":"<item>","kind":"task","risk":["schema"]}

  corrects_note_id  uuid REFERENCES note,     -- a correction supersedes, it does not append
  corrects_block_id uuid REFERENCES doc_block,

  consumed_at   timestamptz,
  consumed_by   uuid REFERENCES attempt,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    text
);
CREATE INDEX note_pending_idx ON note (workspace_id, to_item_id) WHERE consumed_at IS NULL;
CREATE INDEX note_selector_idx ON note USING gin (to_selector jsonb_path_ops)
       WHERE consumed_at IS NULL;
```

`corrects_note_id` is what removes the 129 `CORRECTION` appends: a correction **links to and
supersedes** the thing it corrects, so a reader who finds the original also finds the retraction.
Today they are two sections 500 lines apart and only one of them is ever found.

---

## 10. Claims — one writer at a time, enforced

```sql
CREATE TABLE claim (
  id           uuid PRIMARY KEY,
  item_id      uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  serialization_key text NOT NULL,   -- copied from the owning run_config at claim time
  writes       boolean NOT NULL DEFAULT true,   -- read-only recon claims do not serialize
  agent_name   text NOT NULL,
  agent_token_id uuid,
  role         actor_role NOT NULL,
  acquired_at  timestamptz NOT NULL DEFAULT now(),
  heartbeat_at timestamptz NOT NULL DEFAULT now(),
  expires_at   timestamptz NOT NULL,
  released_at  timestamptz,
  release_reason text
);
CREATE UNIQUE INDEX claim_one_live_per_item ON claim (item_id) WHERE released_at IS NULL;

-- One writer per working tree. Two runs in separate worktrees proceed in parallel;
-- two runs sharing a tree do not. This is the parallel-orchestrator rule, as an index.
CREATE UNIQUE INDEX claim_one_writer_per_tree
  ON claim (serialization_key) WHERE released_at IS NULL AND writes;
```

That second index is *"writing agents run one at a time, never two in parallel"* as a database
constraint rather than a paragraph — and, correctly, it does **not** block the parallel runs you
asked for, because they carry different `serialization_key`s.

An expired lease is not silently reaped — it surfaces in `attention` and, on the next
`work_status`/`work_brief`, is released with a `claim_expired` event. `nested-claude`'s
*"a parked agent is indistinguishable from a busy one"* becomes *"a parked agent stops heartbeating
and the store says so."*

---

## 11. The journal

```sql
CREATE TABLE event (
  seq          bigserial PRIMARY KEY,          -- global cursor for sync and SSE
  workspace_id uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  item_id      uuid REFERENCES item ON DELETE CASCADE,
  at           timestamptz NOT NULL DEFAULT now(),
  actor        text NOT NULL,
  role         actor_role NOT NULL,
  kind         text NOT NULL,
  payload      jsonb NOT NULL DEFAULT '{}',
  summary      text                            -- one compact line, pre-rendered
);
CREATE INDEX event_ws_seq_idx   ON event (workspace_id, seq DESC);
CREATE INDEX event_item_seq_idx ON event (item_id, seq DESC);
```

Event kinds: `created`, `moved`, `linked`, `status_changed`, `forced_transition`, `claimed`,
`claim_expired`, `briefed`, `reported`, `verified`, `judged`, `committed`, `assumption_opened`,
`assumption_resolved`, `finding_raised`, `finding_closed`, `note_added`, `note_consumed`,
`constraint_added`, `replanned`, `escalated`, `incident`, `imported`.

Append-only; never updated. `event.seq` is the cursor for the web UI's SSE stream and for
`work_history(since_seq)` — which is how a compacted orchestrator asks *"what happened while I was
gone"* instead of re-reading a file.

A trigger emits `pg_notify('workstate', json_build_object('ws', workspace_id, 'seq', seq, ...))` so
the API can push without polling.

---

## 12. Tokens

```sql
CREATE TABLE agent_token (
  id            uuid PRIMARY KEY,
  workspace_id  uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  scope_item_id uuid REFERENCES item ON DELETE CASCADE,   -- null = whole workspace
  role          actor_role NOT NULL,
  agent_name    text NOT NULL,
  token_hash    bytea NOT NULL,        -- SHA-256; the plaintext is returned once, never stored
  issued_at     timestamptz NOT NULL DEFAULT now(),
  expires_at    timestamptz,
  revoked_at    timestamptz,
  last_used_at  timestamptz
);
CREATE UNIQUE INDEX agent_token_hash_idx ON agent_token (token_hash);
```

The token determines the **tool list** (§6 of the MCP surface doc), the **scope** of every query, and
the `actor` on every event — so attribution is not self-declared. Compare with
`FixedTimeEquals`, per the workspace's service standard.

---

## 13. Views the server leans on

```sql
-- per-item rollup, used by work_tree and the board. The four unclosed buckets plus done_count
-- sum to the denominator of every "X of Y": 'review' is work in flight and counts, while
-- 'cancelled' and 'superseded' are in no bucket — they are closed and were not done.
CREATE VIEW item_rollup AS
SELECT p.id,
       count(*) FILTER (WHERE c.status IN ('open','ready'))  AS open_count,
       count(*) FILTER (WHERE c.status = 'in_progress')      AS doing_count,
       count(*) FILTER (WHERE c.status = 'blocked')          AS blocked_count,
       count(*) FILTER (WHERE c.status = 'review')           AS review_count,
       count(*) FILTER (WHERE c.status = 'done')             AS done_count
FROM   item p LEFT JOIN item c ON c.ancestors @> ARRAY[p.id]
GROUP  BY p.id;

-- everything unclosed that is not a task: the work_threads backbone
CREATE VIEW open_thread AS
  SELECT 'assumption' AS t, a.ref, a.statement AS text, a.scope_item_id AS scope, a.workspace_id
    FROM assumption a WHERE a.state = 'open'
  UNION ALL
  SELECT 'finding', i.ref, i.title, i.parent_id, i.workspace_id
    FROM item i WHERE i.kind = 'finding' AND i.status NOT IN ('done','cancelled','superseded')
  UNION ALL
  SELECT 'followup', i.ref, i.title, i.parent_id, i.workspace_id
    FROM item i WHERE i.kind = 'followup' AND i.status NOT IN ('done','cancelled','superseded')
  UNION ALL
  SELECT 'note', coalesce(fi.ref,'—'), n.body, n.to_item_id, n.workspace_id
    FROM note n LEFT JOIN item fi ON fi.id = n.from_item_id WHERE n.consumed_at IS NULL;
```

---

## 14. What the schema makes impossible

| Corpus failure | Prevented by |
|---|---|
| Duplicate task number `2.1` in two places | `UNIQUE (ref_scope_id, ref)` — per run, which is where it does harm |
| A task with no verification command | `item_kind.requires_verify` + a refused `work_create` |
| A "done" task with unchecked criteria | the `done` guard reading `item_criterion` |
| A "done" task verified only by its own worker | `verification.run_by` on the guard |
| 17/25 runs with stale checkboxes | there is no second copy of status |
| A superseded task still greppable and still collidable | `status = 'superseded'` + the unique ref |
| A correction 500 lines from what it corrects | `note.corrects_note_id` |
| An assumption closed by strikethrough, or never revisited | `assumption.state` + `revisit_item_id` |
| A deferred follow-up that evaporates | deferral **creates an item**; nothing is a status |
| Two writing agents in one repo | the claim's partial unique index over the run subtree |
| A verify command that can never go red | `verification (workspace_id, command, passed)` |
| "The record disagrees with the code" | the record *is* what the transitions produced, and every transition required evidence |
