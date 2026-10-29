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

`project_config.repo_root` is written only by the importer and read only as a claim's fallback tree.
It is not how a session finds its project; `project_repo` is.

### Project per repository — `project_repo`

**One project per repository, bound on the server.** A project may hold several repositories; a
repository belongs to one project per workspace. A session finds its project from its repo, never
from a title. A **root** is a path, and a path names a folder on one person's machine: two users'
`p:/projects/x` are two checkouts, so a root is bound per workspace **and user**. A **remote** names
the same repository for everyone and is bound once per workspace:

```sql
CREATE TABLE project_repo (
  id              uuid PRIMARY KEY,
  workspace_id    uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  project_item_id uuid NOT NULL REFERENCES item ON DELETE CASCADE,   -- kind = 'project'
  user_id         uuid,              -- a root: the binder's user; a remote, or a credential with no user: NULL
  kind            text NOT NULL CHECK (kind IN ('remote','root')),
  key             text NOT NULL,                                     -- normalised, below
  added_at        timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (user_id, workspace_id) REFERENCES tenant_membership ON DELETE CASCADE,
  UNIQUE NULLS NOT DISTINCT (workspace_id, user_id, key)
);
-- agent_session gains repo_remote, repo_root: the keys a launcher minted the session with.
```

**Identity.** A repository is known by up to two keys, and either one matching is enough:

- **remote** — `git remote get-url origin`, normalised: strip the scheme, `user@`, a trailing `.git`
  and `/`; the scp form `host:owner/name` becomes `host/owner/name`; lowercase.
  `git@github.com:r-Larch/skills.git` and `https://github.com/r-Larch/skills` are both
  `github.com/r-larch/skills`. A repo with no `origin` has no remote key.
- **root** — the **main** checkout's root: the parent of
  `git rev-parse --path-format=absolute --git-common-dir`, which is the same for every worktree of
  the repo (worktrees live outside the root, so a cwd prefix would not match). Normalised: forward
  slashes, no trailing `/` or `/.git`, and a Windows path (drive letter or UNC) lowercased whole.

The server cannot run git, so the client computes the keys and the server normalises whatever it
receives. The rule exists twice — `RepoKeys` in `WorkState.Core/Domain/Repos.cs` and
`nested-claude/server/repo.mjs` — and both test suites run the one table in
[`repo-keys.json`](repo-keys.json).

**Binding.** `work_create(items:[{kind:"project", …, repo:{remote, root}}])` binds the keys it is
given; `work_update(ref:"<project>", repo:{…})` adds more. A root is bound for the caller's user
(`Actor.UserId`; NULL for a static token or a local credential, which is how every row was before
m60); a remote always with NULL. A key already bound to another project — a remote by anyone, a root
by the same user — is refused, naming it: *this repo is project X — `work_brief(scope:"X")`*. There
is no unbind tool. `work_get` of a project prints its remotes and the caller's own roots; another
user's root is a path on their machine and is not shown. `work_tree` rows never print repos. The v1
overview carries them, filtered the same way, as `projects[].repos`.

**Resolution — `work_brief` with no `scope`:**

1. `repo:` given → the **project brief** of the project bound to it ([06 §4a](06-rendering.md)) — a
   remote anyone bound, a root the caller bound (a caller with no user: a root bound with none),
   and only a project the caller may see (§15b) —
   whose `NEXT` names the run to resume — the open run with the most unfinished work — or the call
   that starts one. When the two keys name different projects, the remote wins.
2. no `repo:`, but the caller is a minted session minted with one (`POST /api/agent-sessions
   {…, repo:{remote, root}}`, which `agent_launch` always sends) → the same, from the stored keys.
3. a repo is known but unbound → onboarding naming `work_create(… repo: <the same keys>)`, and
   `work_update(ref:"<project>", repo:…)` with the projects that exist.
4. no repo known, and at most one live project → the busiest run of the workspace, plus one line:
   pass `repo:`.
5. no repo known, and several live projects → **no run is opened.** A chooser: `NEXT
   work_brief(repo:{remote, root})`, `OR work_brief(scope:"<project>")` for each project, newest
   activity first, eight shown and `+N more — work_search(q:"…")` for the rest. Live means not
   archived, cancelled or superseded. It is resolved before the brief's housekeeping, so it sweeps
   no claim and consumes no note.

`scope` given → unchanged; the repo plays no part.

**Other unscoped reads.** `work_next`, `work_tree`, `work_threads`, `work_audit`, `work_search`'s and
`work_history`'s attention block, and the fixed `work://` resources resolve a missing scope the same
way for a minted session with a bound repo: that project's busiest run. Any other caller keeps the
busiest run of the workspace. `work_get`'s attention block is that of the run its first ref sits in.

> **Changed 2026-10-26 (bare-brief).** Step 4 applied to every workspace: a hand-started session in
> one repo was handed another project's run with a bare `work_brief()`, and a minted session's
> `work_next()` read the workspace's busiest run rather than its own project's.

> **Changed 2026-10-26 (project-brief).** Steps 1 and 2 opened the bound project's busiest run. A
> fresh session in the repo then saw one run's brief and nothing of the others, of what had closed
> or of the backlog; it now opens the project, and resumes a run in one more call.

> **Changed 2026-10-29 (m60 machine-keys).** A root was bound once per workspace, so user B's
> `work_brief(repo:{root})` found the project user A had bound the same path to, and B could not bind
> their own checkout at that path to another project. Migration `MachineKeys` adds `user_id` and
> rebuilds the unique key as `(workspace_id, user_id, key) NULLS NOT DISTINCT`. Every existing root is
> copied to every user who can see its project — its members, or every member of the workspace when it
> has none — and the NULL row stays, so a user finds their copy and a credential with no user the
> original: nothing that resolved before stops resolving. Remotes are untouched.

### Archived projects — `item.archived_at`

With a project per repository the list grows to hundreds, so a project can be **archived**: hidden,
never deleted, and reversible.

```sql
ALTER TABLE item ADD COLUMN archived_at timestamptz NULL
  CONSTRAINT item_archived_only_project CHECK (archived_at IS NULL OR kind = 'project');
```

- **Who.** An orchestrator or controller: `work_update(ref:"<project>", archived:true|false)`. The
  operator: the dashboard's Archive / Unarchive button (`POST /api/projects/{id}/archive|unarchive`,
  doc 10). Either journals an `archived` / `unarchived` event with who did it. Archiving what is
  already archived changes nothing and journals nothing.
- **Hidden from** the default lists: the dashboard's sidebar and overview (behind an *Archived (n)*
  toggle), the onboarding list of projects to bind a repo to, the project chooser of a bare
  `work_brief` (an archived project is neither counted nor listed), and the busiest-run fallback
  (an archived project's runs rank after every live one).
- **Still resolves.** By ref (`work_brief(scope:"x")`), and by repo (`work_brief(repo:…)` opens it):
  the brief's header line ends `· archived <date>`. The v1 overview still lists it, with `archivedAt`.
- **New work un-archives.** `work_create` of any item under an archived project clears `archived_at`
  in the same transaction and journals `unarchived` naming the item that brought it back.

A project's **last activity** is the newest journal event anywhere in its subtree; the v1 overview
lists projects newest activity first.

### Ref scoping — short handles that cannot collide across parallel runs

Agents type `2.3` all day; they will not type `gaia/entity-store/2.3`. But two runs in one project
both have a `2.3`. So **`ref` is unique within its nearest ref-scoping ancestor**, not globally:

- kinds marked `scopes_refs` (default: `project` and `run`) open a new ref namespace;
- `item.ref_scope_id` denormalises the nearest such ancestor, maintained by the same trigger as
  `ancestors`;
- the handle an agent passes is `"2.3"` when the call carries a run context (from its token or an
  explicit `run` argument), or the qualified `"entity-store/2.3"` from anywhere.

`UNIQUE NULLS NOT DISTINCT (workspace_id, ref_scope_id, ref)` still makes plan-audit check #3
unviolatable — now per run, which is the scope in which duplicate task numbers actually cause harm.
`workspace_id` is in the key so that the few rows with no ref scope (an assumption on no item, a
feature with no parent stored before m63) are unique per workspace rather than across every workspace on the server.
The key compares `ref` case-sensitively; the server's free-ref probes compare `lower(ref)`.

**A ref-scoping row is numbered among its siblings.** A project or a run is its own ref scope
(`ref_scope_id = id`), so the key above never compares two of them. Their refs are unique among the
self-scoping rows with the same `parent_id` — the runs of one project or feature; the projects of one
workspace, whose parent is NULL — ignoring case:

```sql
CREATE UNIQUE INDEX item_sibling_ref_unique ON item (workspace_id, parent_id, lower(ref))
  NULLS NOT DISTINCT WHERE ref_scope_id = id;   -- ScopeRefUnique (m56)
```

`ref_scope_id = id` holds for exactly the `scopes_refs` kinds, so the predicate names no kind. A
server-numbered run is `1`, `2`, … (under a feature `<feature>.1`), a project `project-1`, … — the first
ref free among those siblings and in the parent's scope, since `gaia/1` reaches both. Another
workspace's rows never count. A duplicate is refused naming the sibling that holds the ref.

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
attach-anywhere kinds `finding`, `followup`, `question`, `incident`, `spike`. A `task` may also sit under
a `task` (`{phase,run,task}`, migration TaskUnderTask, m63): tasks nest in practice, and each keeps its
own criteria and verify guard.

**Root = project.** The kind with an empty `allowed_parent_kinds` is a root, and only `project` has one;
every other kind needs a parent of a kind its list names. A root that is no project would sit outside
every project and so outside project access: no members, visible to the whole workspace. `work_create`,
`work_move` and `work_import` all check this one rule (doc 07 §1). The rows are seeded by InitialTree
only; a later change to them is a migration, which a fresh database runs too.

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

- An orchestrator's unscoped reads default to its **own work** (`work_next`/`work_brief`); its writes
  are not fenced to its run — cross-project writes are intended on a single-user server (doc 01 §6).
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

  CONSTRAINT item_ref_unique      UNIQUE NULLS NOT DISTINCT
                                  (workspace_id, ref_scope_id, ref),   -- audit check #3, unviolatable
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
  CONSTRAINT assumption_ref_unique UNIQUE NULLS NOT DISTINCT (workspace_id, ref_scope_id, ref),
                                -- 'A1' exists once PER RUN, not once globally. With many
                                -- orchestrators running at once, a global unique would make the
                                -- second run unable to open its own A1. A workspace-wide A1
                                -- (ref_scope_id NULL) exists once per workspace.
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
  ref            text NOT NULL,   -- 'C1', 'C2', … the handle the BINDING block prints
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
CREATE UNIQUE INDEX constraint_ref_unique ON constraint_rule (workspace_id, ref);
```

**The ref is numbered per WORKSPACE, not per run** — which is where it parts company with `item.ref`
and `assumption.ref`, both of which are numbered inside their `ref_scope_id`. The reason is the
rendering: one BINDING block shows the workspace-wide rules *together with* every rule scoped to the
item's ancestors, so a per-run number would print two `C1`s side by side, and a ref that
is ambiguous where it is read is not a ref. A1 is scoped to a run because two parallel orchestrators
must each be able to open their own; C1 has no such pressure, because a rule is written once and
read by everyone.

Three things this enables that a file cannot:

- **Citation.** `C7` is an address: it can be quoted in a brief, questioned in a finding, and retired
  by `work_constrain(ref:"C7", retire:true, reason:…)`. A rule you can read but cannot name is one
  you cannot retire, and the corpus is full of rules that outlived their reason because retiring them
  meant editing a markdown file nobody owned.
- **Scoped rendering.** A brief for item X gets the workspace rules plus every rule scoped to X's
  ancestors — no more *"repeating the same fifteen safety lines in twelve briefs"*, and no more
  briefs missing the rule that was added after they were written.
- **Coverage checking** (plan-audit check #6). `covers_risk` vs the `risk_tags` of *upcoming* items
  answers *"does `CONVENTIONS.md` still forbid everything the remaining tasks can destroy?"* as a set
  difference. The `route` column is `nested-claude`'s hardest-won lesson made structural: a
  prohibition row without a route is itself a defect, because *"a rule that demands a capability the
  agent does not have is a trap you set yourself."* `work_constrain` refuses a `red_line` that
  carries no `route` — the one kind that is a prohibition, and the one case where the guard is
  satisfiable every time, since *"stop and report it"* is a route.

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
  to_selector   jsonb,          -- unused: no brief delivers by rule, so work_note refuses a selector

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

> **Changed 2026-10-26 (note-selector).** `work_note(to:"phase:3")` used to store `to_selector`
> and answer "renders into work_brief", but no brief ever read the column. `work_note` now refuses
> a `to` that is not a ref resolving to one item and routes to `work_get` / `work_tree` to find it.
> Rows already holding a selector stay as they are.

`corrects_note_id` is what removes the 129 `CORRECTION` appends: a correction **links to and
supersedes** the thing it corrects, so a reader who finds the original also finds the retraction.
Today they are two sections 500 lines apart and only one of them is ever found.

### 9a. Import provenance — `import_record`

One row per record `work_import` wrote (doc 01 §3.5), keyed by where it came from:

```sql
CREATE TABLE import_record (
  id              uuid PRIMARY KEY,
  workspace_id    uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  project_item_id uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  source_path     text NOT NULL,              -- repo-relative, forward slashes
  source_anchor   text NOT NULL DEFAULT '',   -- a heading or L<line>; '' = the whole file
  kind            text NOT NULL,              -- run|phase|task|followup|note|assumption|rule
  item_id uuid, note_id uuid, assumption_id uuid, constraint_id uuid,  -- exactly one set
  why             text NOT NULL,              -- why the importer judged it still true
  imported_status text,                       -- the status the import left an item in
  session_id      uuid REFERENCES agent_session ON DELETE SET NULL,
  imported_by     text NOT NULL,
  created_at, updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (project_item_id, source_path, source_anchor)
);
```

The unique key is what makes a re-import an update. `imported_status` is what "moved since import"
is judged against: an item whose status differs, or that was started, checked or verified, is never
rewritten. An imported item also carries `item.source = 'import:<path>#<anchor>'`; an imported
assumption's `evidence_note` carries the same plus the why. `work_get` and the v1 item page
(`imported`) print source and why whole.

---

## 10. Claims — one writer at a time, enforced

```sql
CREATE TABLE claim (
  id           uuid PRIMARY KEY,
  item_id      uuid NOT NULL REFERENCES item ON DELETE CASCADE,
  serialization_key text NOT NULL,   -- the tree: work_status's tree:, else run_config, else repo_root, else ref scope
  key_workspace_id uuid,             -- the workspace the lock is held in, for every key
  key_user_id  uuid,                 -- a path key: the claimer's user; a name, or a caller with no user: NULL
  tree         text,                 -- the normalised tree: the caller passed, if any
  writes       boolean NOT NULL DEFAULT true,   -- read-only recon claims do not serialize
  agent_name   text NOT NULL,
  holder_user_id uuid,               -- the user who took it (Actor.UserId), whatever the key; NULL: no user
  agent_token_id uuid,
  role         actor_role NOT NULL,
  acquired_at  timestamptz NOT NULL DEFAULT now(),
  heartbeat_at timestamptz NOT NULL DEFAULT now(),
  expires_at   timestamptz NOT NULL,
  released_at  timestamptz,
  release_reason text
);
CREATE UNIQUE INDEX claim_one_live_per_item ON claim (item_id) WHERE released_at IS NULL;

-- One writer per working tree. Two units in separate worktrees proceed in parallel, even
-- in one run; two sharing a tree do not. The parallel-orchestrator rule, as an index.
-- A path is also per user (key_user_id); a name is per workspace.
CREATE UNIQUE INDEX claim_one_writer_per_tree
  ON claim (serialization_key, key_workspace_id, key_user_id) NULLS NOT DISTINCT
  WHERE released_at IS NULL AND writes AND session_id IS NULL;
-- and claim_one_session_per_tree, the same key with session_id WITH <> (doc 11)
```

That second index is *"writing agents run one at a time, never two in parallel"* as a database
constraint rather than a paragraph — and, correctly, it does **not** block the parallel units you
asked for, because they carry different working trees.

**The key.** `work_status(ref, to:"in_progress", tree:"<worktree path or branch>")` makes the
caller's tree the key. A path is normalised — trimmed; on Windows (`X:\…`, `\\server\…`) slashes
become `/`, case is folded and a trailing slash dropped; a POSIX path keeps its case and loses a
trailing slash. A branch (`refs/heads/` stripped) is a name. Without `tree` the key is today's: the
run's `serialization_key`, else the project's `repo_root`, else the ref scope. Every key locks **within
its workspace** (`key_workspace_id`), a path as much as a name: a path is a place on one tenant's disk
(m50 tenant-per-request; migration `ClaimKeyWorkspace` keyed the live claims taken before). A **path**
— the `tree:` given, or a fallback key that parses as one, such as `repo_root` — is also a folder on one
person's machine, so it locks per **user** too (`key_user_id`, the caller's `Actor.UserId`): user B's
`p:/projects/x` is not user A's, and neither blocks the other. A **name** — a branch, the ref-scope
fallback — is the workspace's and blocks across users. A path claimed by a credential with no user keeps
NULL and is the workspace's, as every path was before m60 (migration `MachineKeys`, which also gave a live
path claim taken by a session with a user that user).

**The holder.** `holder_user_id` is who took the claim, apart from what it locks: `key_user_id` is the
lock's key and is NULL for a name, while users share role names (every OAuth controller is
`controller`). A claim with a session is that session's; one without is the caller's only for the same
`agent_name` and the same `holder_user_id` (`IS NOT DISTINCT FROM`), so NULL matches only a caller
with no user — a static or local token, as before. Migration `ClaimHolderUser` (m62) set it on every
live claim with a session from `agent_session.user_id`; the rest keep NULL.

> **Changed 2026-10-22 (ws/8).** Claims were per run: with no `tree` parameter, every unit of a run
> fell back to the run's key (`30m lease on "m0"`), so a controller running parallel units in
> separate git worktrees was refused the second one. And the index was on `serialization_key` alone,
> server-wide, while the sweep is per workspace — two workspaces with same-named runs shared a lock.
> Migration `ClaimPerTree` adds `key_workspace_id` and `tree`, rebuilds the index over
> `(serialization_key, key_workspace_id) NULLS NOT DISTINCT`, and leaves every existing key as it
> is; a live claim whose key came from the ref-scope fallback gets its workspace, so it keeps binding
> its run across the upgrade.

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

### 12a. Users — `app_user`, `tenant_membership`

```sql
CREATE TABLE app_user (                -- `user` is reserved
  id              uuid PRIMARY KEY,
  name            text NOT NULL,
  password_hash   text NOT NULL,       -- ASP.NET Core PasswordHasher; never the password (C64)
  is_system_admin boolean NOT NULL DEFAULT false,
  created_at      timestamptz NOT NULL DEFAULT now(),
  disabled_at     timestamptz          -- set: cannot sign in, and held cookies stop working
);
CREATE UNIQUE INDEX app_user_name_unique ON app_user (lower(name));

CREATE TABLE tenant_membership (       -- a tenant is a workspace
  user_id      uuid NOT NULL REFERENCES app_user ON DELETE CASCADE,
  workspace_id uuid NOT NULL REFERENCES workspace ON DELETE CASCADE,
  role         text NOT NULL CHECK (role IN ('admin', 'member')),
  created_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, workspace_id)
);
```

The people who sign in to the dashboard (doc 08 §4). The first row is created from the configured
dashboard credential on the first sign-in, and is the only row anything creates so far. Memberships
are recorded but not yet enforced: every request still acts in the configured workspace.

`agent_session.user_id` (m51, migration `AgentSessionUser`; nullable, no foreign key) is the user whose
OAuth token, or whose minted `wss_` token, named or minted the session; a static token's sessions have
none. `agent_session_name_unique` is `(workspace_id, user_id, name) NULLS NOT DISTINCT`, so a name is
unique among one user's sessions and the static tokens share one namespace per workspace (doc 11).

### 12b. Project members — `project_member`

```sql
CREATE TABLE project_member (          -- who may work in a project (m58, migration ProjectMembers)
  project_item_id uuid NOT NULL REFERENCES item ON DELETE CASCADE,       -- an item of kind project
  user_id         uuid NOT NULL,
  workspace_id    uuid NOT NULL,                                          -- the project's; RLS reads it
  added_by        uuid REFERENCES app_user ON DELETE SET NULL,           -- null: migration or static token
  added_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (project_item_id, user_id),
  FOREIGN KEY (user_id, workspace_id) REFERENCES tenant_membership ON DELETE CASCADE
);
```

A project's members, like a case's users in Nomos: every member has full read and write, and a
project with every member of its workspace is the tenant-wide one (doc 08 §4 "Projects"). The rules:

- **A member is a member of the project's workspace.** The foreign key to `tenant_membership` makes
  it so in the database, and it is why removing a user from a workspace removes their project
  memberships there — the rows go with the membership. The code checks it too, before it adds
  anyone, and refuses a user of another workspace as unknown. That the row's `workspace_id` is its
  project's is the writer's: every insert copies it from the item, and the policy below keeps it in
  the caller's tenant.
- **New projects are private.** `work_create(kind:"project")` makes the caller's user its one member
  (`CallerIdentity.UserId`: an OAuth token or a user-minted `wss_` session). With no user — a static
  token, the offline importer — every current member of the workspace becomes a member instead. A user
  who is not in the workspace (a system administrator visiting it) is refused (m59): they could not be a
  member, so the project would be hidden from them the moment it existed (§15b).
- **The migration** gave every project that existed every member of its workspace (`added_by` null):
  nothing that was visible to a member before became private.
- **A project keeps one member.** Removing its last member is refused, and so is removing that user
  from the workspace (`AdminStore`), naming the projects; both take the workspace row's lock, so the
  two cannot each remove one of a project's last two members. Disabling a user removes nothing.
- **It decides who sees the project** (m59, §15b): a project's rows reach its members only, enforced in
  the database on every read and write path.

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

-- everything unclosed that is not a task: the work_threads backbone. work_threads lists only the
-- notes that name a to_item_id: no call consumes the others (doc 01 §3.1, ws/11).
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
| Duplicate task number `2.1` in two places | `UNIQUE (workspace_id, ref_scope_id, ref)` — per run, which is where it does harm |
| A task with no verification command | `item_kind.requires_verify` + a refused `work_create` |
| A "done" task with unchecked criteria | the `done` guard reading `item_criterion` |
| A "done" task verified only by its own worker | `verification.run_by` on the guard |
| 17/25 runs with stale checkboxes | there is no second copy of status |
| A superseded task still greppable and still collidable | `status = 'superseded'` + the unique ref |
| A correction 500 lines from what it corrects | `note.corrects_note_id` |
| An assumption closed by strikethrough, or never revisited | `assumption.state` + `revisit_item_id` |
| A deferred follow-up that evaporates | deferral **creates an item**; nothing is a status. `"backlog"` is a run under the project (a plain `item` of kind `run` with a `run_config`), so the run the work left can still close (doc 07 §4, changed 2026-10-22 ws/12) |
| Two writing agents in one repo | the claim's partial unique index over the run subtree |
| A verify command that can never go red | `verification (workspace_id, command, passed)` |
| "The record disagrees with the code" | the record *is* what the transitions produced, and every transition required evidence |
| A query that forgets its `workspace_id` filter | row-level security: the database returns the request's tenant's rows and no other's (§15) |
| A query that forgets the caller is not a member of the project | `project_access`: the database hides a project's rows from everyone but its members (§15b) |

---

## 15. Row-level security — the database keeps the tenant

m54, migration `TenantRls`. Every request has exactly one tenant (`RequestTenant`, claim
`workstate:tenant`), and every query names it in its own SQL. Row-level security is the second line:
the database itself returns only that tenant's rows, so a query that forgets its filter leaks nothing,
and a write that names another workspace is refused.

### The role and the setting

```sql
CREATE ROLE workstate_app NOLOGIN;     -- cluster-wide; created once, shared by every database that migrates
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO workstate_app;   -- and its sequences
REVOKE ALL ON "__EFMigrationsHistory", data_protection_key, oauth_key, openiddict_* FROM workstate_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO workstate_app;

ALTER TABLE item ENABLE ROW LEVEL SECURITY;
ALTER TABLE item FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON item
  USING      (workspace_id = (SELECT NULLIF(current_setting('app.workspace_id', true), '')::uuid))
  WITH CHECK (workspace_id = (SELECT NULLIF(current_setting('app.workspace_id', true), '')::uuid));
```

The tenant is a scalar subquery so that a plan reads it once, as an InitPlan, and not once per row
(doc 15 §7).

The server never logs in as `workstate_app`. It connects as it always did, as `POSTGRES_USER` — the
image's superuser and the tables' owner, which no policy confines — and every request-path transaction
begins with

```sql
SELECT set_config('role', 'workstate_app', true), set_config('app.workspace_id', $tenant, true);
```

Both are **transaction-local**: the commit or rollback that ends the transaction ends them, so a pooled
connection goes back to the pool as the owner with no tenant. That does not depend on Npgsql's
reset-on-return: it sends `DISCARD ALL` only while no statement is prepared, and the `RESET ALL` it falls
back to leaves `role` set. `RlsTests` proves it with reset switched off.

`NULLIF(…, '')` is load-bearing. Unset, the setting reads `NULL`; but once a transaction that set it has
ended, it reads `''` on that connection, and `''::uuid` is an error rather than "no tenant". With the
`NULLIF` both read as no tenant, which admits no row and refuses every write.

No new password or secret, and the deploy's Postgres user is unchanged. That user must **stay a
superuser**: the bypass relies on it, and so does `LEAKPROOF` below. `POSTGRES_USER` of the postgres
image is one.

### The tables

| policy | tables |
|---|---|
| its own column, `workspace_id = tenant` | `item`, `item_link`, `doc`, `event`, `assumption`, `constraint_rule`, `note`, `verification`, `project_repo`, `agent_token`, `agent_session`, `import_record`, `tenant_membership`, `user_link` (a reset link has no workspace, so no tenant sees it), `project_member` (m58, created with its policy by its own migration) |
| its own id, `id = tenant` | `workspace` |
| its parent `item` is the tenant's | `item_criterion`, `attempt`, `artifact`, `claim`, `project_config`, `run_config` |
| through two parents | `report` (→ `attempt` → `item`), `doc_block` (→ `doc`), `doc_block_revision` (→ `doc_block` → `doc`) |
| none — global, read whole | `app_user` (a user spans workspaces), `item_kind` (the vocabulary) |
| none — and not granted to the role | `__EFMigrationsHistory`, `data_protection_key`, `oauth_key`, `openiddict_*` |

A child table's policy is `EXISTS (SELECT 1 FROM item p WHERE p.id = <child>.item_id AND p.workspace_id
= tenant)`. No column was added: no writer changes, and nothing can disagree with the parent. `claim`
has `key_workspace_id`, but it is nullable, so it too goes through its item.

Both views, `item_rollup` and `open_thread`, are `security_invoker = true`. A view otherwise reads its
tables as its owner, which the policies do not confine.

`FORCE ROW LEVEL SECURITY` is on every tenant table. With the image's superuser as owner it changes
nothing: a superuser passes every policy, FORCE or not. It is what would confine an owner that is *not*
a superuser, should one ever run these tables (`RlsTests` checks exactly that). Such an owner is not
supported: the bypass below and `LEAKPROOF` both need a superuser.

`RlsTests` reads the catalogue, so a table added later cannot slip past. Every table must be a tenant
table, forced and with its policy, or one of the global or server-owned tables above. Every view must
run as its caller. A tenant table added after TenantRls is created with its policy by its own
migration and listed in `TenantTables` (`WorkState.Core/Data`), which the tests read; TenantRls's own
lists stay what that migration applied.

### LEAKPROOF — the indexes a policy would otherwise switch off

```sql
ALTER FUNCTION arraycontains(anyarray, anyarray) LEAKPROOF;   -- ancestors @> ARRAY[id]: item_subtree_idx
ALTER FUNCTION lower(text) LEAKPROOF;                         -- lower(ref) = …: item_ref_lower_idx
ALTER FUNCTION ts_match_vq(tsvector, tsquery) LEAKPROOF;      -- search @@ …: item_search_idx
ALTER FUNCTION to_tsvector(regconfig, text) LEAKPROOF;        -- to_tsvector('english', body): note_search_idx
```

Under a policy, Postgres evaluates a query's own condition before the policy's only if the condition's
function is `LEAKPROOF`, meaning it cannot reveal a row through an error or a side channel. That includes
using the condition as an index condition. None of these four is marked so by default. Without them, as
the tenant role, every subtree count, ref lookup and search scanned the whole workspace by its
`workspace_id` index and filtered row by row. The rollup and document-path laterals, which run once per
row, turned that into a quadratic read: the board read went from 3 ms to 38 ms of server time on the live
copy (doc 15 §7).

**Why it is safe here.** `LEAKPROOF` protects a table from a caller who writes their own predicate and
learns from its errors which rows the policy hid. WorkState has no such caller. Every statement is SQL the
server itself wrote, with values bound as parameters, and no tool, endpoint or argument runs
caller-supplied SQL. The policies guard against the server's own forgotten filter (above), which is
unaffected. These functions may now run on a row before the policy does, which is what lets them drive
an index. They return a boolean or a vector, and the policy still decides which rows come back. They are
not leakproof in the strict sense: `to_tsvector` raises on a value too long for a `tsvector`, which is why
Postgres does not mark it so. An error like that can only tell something to a caller who writes their
own SQL. The flags are set in
this database only (`pg_proc` is per-database), on built-in functions whose results do not depend on
who calls them.

**What it needs.** Only a superuser may mark a function `LEAKPROOF`, so the migration requires the owner
to be one, as the bypass already does. `Down` marks all four `NOT LEAKPROOF` again. `pg_dump` does not
carry attributes of built-in functions: a database restored from a dump keeps its migration history and
loses the flags. So `SchemaOwner` re-asserts them at every start, after migrating. It logs one warning
line naming the functions when it had to, and nothing when they were already set. A deployment that sets
`WorkState:Migrate=false` must run the four `ALTER FUNCTION`s itself.
`RlsTests.Rls_the_subtree_ref_and_search_indexes_still_serve_reads_under_the_tenant_role` fails if any of
the four indexes stops serving its predicate as the tenant role.

### Connections — the tenant helper, and the bypass by name

`WorkStateConnections` (WorkState.Core/Data) is the only code that opens a connection or begins a
transaction. `RlsSourceGuardTests` fails on `OpenConnection`, `BeginTransaction`, `new NpgsqlConnection`
or `dataSource.CreateCommand` anywhere else in the server. It also fails on an EF context outside the
schema owner, OpenIddict's registration and the offline importer.

- **`TenantAsync()`** — a transaction acting as `workstate_app` in the tenant of the ambient
  **`TenantScope`**. The API enters that scope in one middleware right after `UseAuthorization`, which
  is where an endpoint policy's schemes (the cookie, OAuth) put their principal on the request. A
  scope with no tenant, or no scope at all, sees no rows; it does not see all rows.
  `BeginTenantAsync(connection)` starts the next tenant transaction on the same connection, once the
  first has ended (`WorkWriter`'s re-read after a refused claim).
- **`OwnerBypassAsync(Bypass why)`**, and `OwnerBypassTransactionAsync` for a bypass that writes more
  than one statement — the owner, which RLS does not confine. `Bypass` is a closed enum, so the bypass
  list below is the code's own:

| `Bypass` | path | why it cannot run as the tenant |
|---|---|---|
| `Authentication` | `SessionStore.AuthenticateAsync` (a `wss_` token → its session) | the token is what names the tenant; there is none yet |
| `WorkspaceBinding` | `BoardQueries.WorkspaceAsync`, `WorkWriter.EnsureWorkspaceAsync` (the static tokens' `StaticTokenTenant`) | the configured workspace is looked up, or created on first use, during authentication (C62) |
| `Users` | `UserStore`: sign-in, the cookie's stamp check, the OAuth user, the tenant switcher, password change | a user and their memberships span workspaces |
| `Administration` | `AdminStore` (`/api/admin/*`) | it acts on the workspace its path names; the caller's rights are read from the table on every call (C62) |
| `Links` | `UserLinkStore` (invite and reset links) | opened by token before anyone signs in; a reset link has no workspace |
| `Keys` | `PostgresXmlRepository`, `OAuthKeyRing` | the cookie and OAuth signing keys belong to the server |
| `Journal` | `EventStream`'s one `LISTEN` connection | it hears every workspace and fans each event out to that workspace's subscribers only |

The owner also runs, outside the helper, the migrations (`SchemaOwner`, at startup), OpenIddict's EF
stores (its tables hold no tenant rows) and the importer (`tools/WorkState.Import`, an offline tool).

One request path chooses its tenants itself: `/r/{ref}` is anonymous to the pipeline and authenticates
the cookie in its handler. It enters the cookie's tenant. To say "this ref is in your other workspace
X", it then enters each other workspace the user may enter (`UserStore.TenantsAsync`), one at a time.

The test suite's direct store calls are no request and enter no scope. The fixture's data source is
marked `RunUnscopedAsOwner`, so they run as the owner, as before. Nothing in the server calls that,
and a guard test enforces it. Every HTTP test runs through the API's own data source, under RLS.
`RlsRequestPathTests` shows a request reaches the database as the role: revoking the role's `SELECT`
on `item` fails `/api/v1/overview`.

### What it is not

A guard against a forgotten filter, not against arbitrary SQL. The session user is the owner, so a
statement that can run anything can also `RESET ROLE`.

The cost is mostly round trips. On server time the gate's reads are within 20% on the live copy and on
the 5k seed: frontier −4% to +6%, rollup +11% to +15%, board −27% to +11%. Four attention and search
reads are not yet: expired claims, done-unverified, stale in-progress and search (doc 15 §7.2). A tenant
read is now a transaction: `BEGIN` rides with the next statement, but the `set_config` and the closing
`ROLLBACK` are a round trip each. Before m54 a read was the statement alone. Sending the `set_config`
with the first statement was considered and left out. Npgsql 10 has no per-open or per-command hook:
`UsePhysicalConnectionInitializer` runs once per physical connection, not per tenant, and nothing public
prepends to the `BEGIN` it holds back. It would take a connection wrapper in place of `NpgsqlConnection`
in every store.

---

## 15b. Project access — the database keeps the project

m59, migration `ProjectAccess`. A project's data is reachable only by its members (C66): within one
tenant, a user who is not a member of a project reads, searches, streams, resolves and writes nothing of
it, and is answered as for a ref or an id that does not exist. As with the tenant (§15), every path still
names what it reads, and the database is the line that holds when one forgets.

### The user, and who has none

The tenant helper sets three more transaction-local settings with the first two, in the same statement:

```sql
SELECT set_config('role', 'workstate_app', true), set_config('app.workspace_id', $tenant, true),
       set_config('app.user_id', $user, true),
       set_config('app.access_seq', coalesce(a.seq, ''), true),
       set_config('app.hidden_projects', coalesce(a.hidden, ''), true)
FROM   workstate_project_access($tenant, $user) a;        -- with no user: '' and '', no call
```

`$user` is the `TenantScope`'s user, which the API enters with the tenant, from the principal and from
nothing else (`RequestUser`): the cookie's `sub`, or the `workstate:user-id` of an OAuth token or of a
`wss_` session a user minted. A principal that names two different users — a cookie beside another
user's bearer token — enters no tenant at all. `/r/{path}` enters the cookie's user, in the cookie's
tenant and in each other one it looks in. The server enters a scope only with
`TenantScope.EnterAs(tenant, user)`; `Enter(tenant)`, which has no user, is the test suite's
(`ProjectAccessRlsTests` guards it).

**A credential with no user sees every project of its tenant**: a static role token, the reader token,
the offline importer (the owner) and the test suite's direct store calls. Static role tokens are for a
local single-user install (doc 08 §4); the server deploy configures none. A reader token configured on a
shared server reads every project of its workspace.

### What is hidden, and what stays the tenant's

A project is **hidden** from the caller when it has members and the caller is not one. An item belongs
to its root — the project at the top of its tree, or itself — so a whole subtree is hidden or not with
its project. Hidden from no one:

- **A project with no member** — a static token's project on an install that has no user yet, or one
  the importer wrote into an empty tenant. It is the tenant's until its first member joins, which makes
  it private from the next statement on; so it is never orphaned.
- **A root that is no project**, which has no members: a parentless feature and everything under it. No
  writer creates one since m63 (only a project is a root, see "Kinds are data"); one stored before stays visible.
- **Workspace rows that name no item**: an assumption, a rule or an agent token with no scope, a
  document with no item, a note addressed to nothing and from nothing, an event with no item. They
  belong to the workspace, not to a project, so every member of it reads them.

A policy `project_access`, `RESTRICTIVE` and so ANDed with `tenant_isolation`, sits beside it on these
tables (migrations `ProjectAccess`, then `ProjectAccessSeq`, which rewrote its predicates):

| policy | tables |
|---|---|
| its root is not hidden | `item` (root `coalesce(ancestors[1], id)`), `project_member` (its project) |
| its item is visible — `EXISTS (SELECT 1 FROM item p WHERE p.id = <column>)`, read under `item`'s policies | `verification` (`item_id`), `project_repo` and `import_record` (`project_item_id`), `item_link` (both ends) |
| the same, or the reference is null: a workspace row | `doc` (`item_id`), `event` (`item_id`), `assumption` and `constraint_rule` (`scope_item_id`), `agent_token` (`scope_item_id`), `note` (`to_item_id` and `from_item_id`) |
| none of its own: its tenant policy already reads its parent, which reads `item` | `item_criterion`, `attempt`, `artifact`, `claim`, `project_config`, `run_config`, `report`, `doc_block`, `doc_block_revision` |
| none — no project | `workspace`, `tenant_membership`, `user_link`, `agent_session` |

`TenantTables.ByProject` lists the first three rows; `ProjectAccessRlsTests` reads it. The views run as
their caller (§15), so `item_rollup` and `open_thread` count and list only what the caller may see.

### `access_seq` — the transaction's answer, while it is still true

Asking who may see a project once per row, or once per statement through a function, cost the board
+65% (doc 15 §8). So the answer is read once, when the transaction begins, and kept only while nothing it
depends on has changed. `workspace.access_seq` moves on every such change, by trigger, in the transaction
that makes it:

| trigger | fires on | why |
|---|---|---|
| `project_member_access_seq_trg` | `project_member` insert, update, delete — a cascade from a tenant membership or an item included | who is a member is the whole answer |
| `item_root_insert_access_seq_trg` | an `item` inserted with no parent | a new project or root, which the answer cannot have named |
| `item_root_move_access_seq_trg` | `item.parent_id` set to or from null | a root made or unmade by a move |
| `item_root_delete_access_seq_trg` | an `item` with no parent deleted | a root gone (nothing deletes items today; the cascade would move it anyway) |

A kind change makes or unmakes no root — a root is an item with no parent, whatever its kind — so it has
no trigger. A move between two existing roots needs none either: each row's root is read from the row.
`workstate_bump_access_seq` runs as the owner, so the bump lands whatever the writer's policies see.

`workstate_project_access(workspace, user)` (owner, `STABLE`) reads the seq and the projects hidden from
the user in one statement, so the list is exactly what that seq stands for. The policies then read:

```sql
-- a root:      item, project_member
no_user OR CASE WHEN unchanged THEN NOT (root = ANY (hidden_at_start))
                ELSE NOT (root = ANY ((SELECT workstate_hidden_projects())::uuid[])) END
-- a row that hangs off an item
no_user OR (unchanged AND nothing_hidden_at_start) OR EXISTS (SELECT 1 FROM item p WHERE p.id = <column>)

-- each an InitPlan, once per statement:
--   no_user                 (SELECT NULLIF(current_setting('app.user_id', true), '') IS NULL)
--   unchanged               (SELECT w.access_seq = NULLIF(current_setting('app.access_seq', true), '')::bigint
--                            FROM workspace w WHERE w.id = <tenant>)
--   hidden_at_start         (SELECT NULLIF(current_setting('app.hidden_projects', true), '')::uuid[])::uuid[]
--   nothing_hidden_at_start (SELECT current_setting('app.hidden_projects', true) = '{}')
```

While the statement reads the seq the transaction began with, the list is the truth: a member of every
project pays nothing per row — no comparison and no item lookup behind a child row. When it reads another,
the policy asks `workstate_hidden_projects()` (owner, `STABLE`), which reads the statement's own snapshot.
An InitPlan runs only when its branch is reached, so that function runs only after a change.

**Fail-closed.** The seq and the change it marks commit together, and every statement reads both from its
own snapshot: it sees the change and the new seq, and asks the live check, or sees neither. So a change
another transaction commits — a member removed, a member added, a project created that the caller is not
in — applies from the caller's **next statement**, inside a transaction that is still open
(`ProjectAccessRlsTests`: removed, created, added). A statement that was already running finishes on its
own snapshot. A setting that is missing reads as null and takes the live branch.

**Why functions, and why the owner.** A policy that read `project_member` itself would need a grant on it
for every role a policy applies to, and `project_member`'s own policy could not read it without recursing.
`SECURITY DEFINER` runs both functions as the owner, the superuser §15 requires, which no policy confines;
each reads only its arguments or the transaction's settings, so no caller can point it elsewhere. The
policies read `workspace` directly, which the tenant role may; an owner that is not a superuser needs the
same grant (`RlsTests` gives its test owner `SELECT` on `workspace`).

**What a bump costs a writer.** The bump updates the workspace row, so two transactions that each add or
remove a member, or create a root, in the same workspace take turns on that row until the first commits.
Every other write only references the row (`FOR KEY SHARE`, which the bump's `FOR NO KEY UPDATE` does not
block), and no reader waits.

### The paths that see every project, and the ones the database cannot keep

- **A project's member list.** `GET|POST|DELETE /api/projects/{id}/members` run in a tenant transaction
  that hides nothing (`WorkStateConnections.TenantEveryProjectAsync(ProjectBypass.Membership)`), because a
  workspace admin manages a project's members without being one. The writer checks that the caller is a
  member or an admin, and reads the project's ref and its members, never its work. The admin pages run as
  the owner (§15) and name a project only in the refusal to remove its last member.
- **The live stream.** `/api/stream`'s one `LISTEN` connection is the owner's. Each event is read back
  through the subscriber's tenant scope before it is sent (`ApiQueries.StreamPlacementAsync`): an event of
  a hidden project is not sent. Each read is its own transaction, so a member added sees the project's
  next event on a stream they already hold, and a member removed stops seeing them from the next one.
  `member_added` and `member_removed` are events of their project and are routed like any other: the user
  removed does not see their own removal.
- **Unique indexes** are checked across every row, visible or not. Three names are workspace-wide and
  can meet a hidden project: a top-level project's ref, a repository key (`project_repo`; a root only
  among one user's own bindings), and a claim's tree key (a path only among one user's own claims). A project ref the server numbers skips a number a hidden project holds (a savepoint, then the
  next number). A ref the caller chose, or a repository another project holds, is refused as taken — the
  repository's by a project the caller is not a member of, never which — so a non-member learns that a
  name is taken and nothing else. A tree a hidden project's claim holds is refused as the index refuses it.
- **Triggers** run as their caller and see what the caller sees. `item_tree_before` refuses a parent the
  caller cannot see as a parent that does not exist. The `blocks` cycle check walks only the links the
  caller can see: a cycle closed through a project the caller cannot see is not caught.

### What it costs

One owner function call inside the `set_config` statement the tenant helper already sends, and a few
InitPlans per table a statement reads. Measured on the live-data copy in doc 15 §8.
