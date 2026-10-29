# 07 — The guards: transitions, claims, and forced closure

> The product's whole claim is that **state which is the primary write gets maintained, and state
> which is narrated does not**. These guards are what make the write primary. Everything else is
> plumbing.
>
> Measured context: across 181 real files, **424 of 502 checkboxes are still open** — 84.5% — in runs
> whose ledgers declare them finished. Nothing refused those closures, so nothing happened.

---

## 1. Where a rule lives

U1 put most of the schema's rules **in Postgres**, with errors that name the fix. That is the right
default and U4 must not re-implement them in C#.

| Rule | Lives in | Why there |
|---|---|---|
| duplicate `ref` in a run · `blocks` cycle · resolution without `done` · `closed_at` consistency · two live write-claims on one tree · an edited `event` | **Postgres** (U1) | Cannot be bypassed, not even by a bug in the tool layer. Catch `PostgresException`, surface its message. |
| the `done` **pre-conditions** (criteria, verification, children, findings, commit) | **the tool layer** (U4) | They are a *policy* with an override, and they need to produce a multi-line remedial message. A constraint can only say no. |
| claim **expiry** | **both** — the unique index in Postgres, the TTL sweep in the tool layer | The index makes overlap impossible; the sweep makes expiry *visible*. |

**The test of where a rule belongs:** if violating it should be impossible, it is a constraint. If
violating it should be possible but recorded, it is a guard.

**A constraint firing is a refusal, never an exception** (doc 04 §3). Every write path that can reach
one catches the `PostgresException` and answers `REFUSED` with a callable route. Where the tool layer
can name more than the schema message — a claim index firing names the item holding the claim, its
agent and when its lease lapses — it re-reads and says so. And because a concurrent writer can take a
ref, seq or claim between any check and its insert, the MCP write tools carry a last net: an uncaught
`PostgresException` comes back as `REFUSED … — the schema refused it.` with *retry the call* as its
first route, not as *"An error occurred invoking …"*.

---

## 2. The transition table

```
        ┌──────────────────────────── work_status(to:) ────────────────────────────┐
open ──▶ ready ──▶ in_progress ──▶ review ──▶ done
  │        ▲            │             │
  │        └────────────┴─────────────┘   (rework: review/in_progress ──▶ in_progress)
  │                     │
  ├──▶ blocked ─────────┘
  │
  ├──▶ cancelled          (terminal, requires reason)
  ├──▶ superseded         (terminal, ONLY via a `supersedes` link — never set directly)
  └──▶ deferred           NOT A STATUS. A move. See §4.
```

| From | To | Guard |
|---|---|---|
| `open` | `ready` | every `blocks` predecessor is `done`/`cancelled`/`superseded`. Otherwise refused, naming them. |
| `ready`/`open` | `in_progress` | **acquires a claim** (§5) on the working tree: `tree:` if given (a worktree path or branch, normalised — doc 02 §10), else the run's key. Refused if another live claim holds this item, or a write-claim holds this tree — **including one the same agent holds on another item**. Every key locks within its workspace, a path as much as a name: another tenant's claim on the same tree never holds it. The refusal names the holding item (qualified ref), its agent and when its lease lapses, and the moves: release it (`work_status(holder, review)`), retake once the lease lapses (with the same `tree:`), claim your own worktree with `tree:` if none was given, or work another tree. Only the holder re-taking its own item passes: that extends the lease. |
| `in_progress` | `blocked` | requires `reason`. Records a `blocked` event. |
| `in_progress` | `review` | free. |
| `review`/`in_progress` | `done` | **the five conditions, §3.** |
| `ready`, once started | `done` | **the five conditions, §3**, unchanged. Only for an item with `started_at` set — one whose lease lapsed and the sweep returned to `ready` (§5). A never-started `ready` item is still refused: `done is not reachable from ready`. |
| `review`/`in_progress` | `in_progress` | rework. Records the attempt count; **a fresh worker, not the old one** — that is orchestration doctrine, not a schema rule. |
| any non-terminal | `cancelled` | requires `reason`. `destructiveHint: true`. |
| any | `superseded` | **not directly.** `work_link(supersedes)` sets it in the same transaction (U1 trigger), and the same transaction moves the target's unconsumed notes to the replacing item (F-08). |
| any non-terminal | `deferred` | **a move, §4.** |

**Releasing the claim** happens automatically on leaving `in_progress` by any route.

> **Changed 2026-10-26 (lease-ready).** An unnamed controller's 30-minute lease lapsed while a nested
> agent worked for hours; the sweep moved the item to `ready`, and `done` was then refused *"not
> reachable from ready"* until a ceremonial `work_status(in_progress)`. A lapsed lease is a timer, not
> a return to the queue, so a started item now closes from `ready` — every §3 condition still applies,
> and the evidence it reads is all of the item's verifications, not those of the current claim.

> **Changed 2026-10-22 (ws/8).** The `in_progress` row locked the run: every unit fell back to the
> run's key, so two units of one run in separate git worktrees could not both be in progress — the
> shape `/rlarch:controller` runs. `work_status` now takes `tree:`; one writer per worktree, many per
> run. Without it the behaviour is unchanged, except that a ref-scope fallback key no longer collides
> with a same-named run in another workspace. An expired claim's attention line names its tree and
> retakes with it.

> **Changed 2026-10-28 (m50 tenant-per-request).** A path — a `tree:` path, a run's key, a project's
> repo root — locked server-wide, so a lapsed claim another workspace had not swept refused the same
> tree here. A path is a place on one tenant's disk: every key is now keyed by workspace, and migration
> `ClaimKeyWorkspace` gave the live claims taken before it their item's workspace.

---

## 3. The five conditions for `done`

Refused unless **all** hold. Each maps to a rule the corpus shows being broken:

| # | Condition | The corpus failure it prevents |
|---|---|---|
| 1 | every `item_criterion` is `checked` | 424 unchecked boxes in "finished" runs |
| 2 | ≥1 `verification` with `passed = true` and **`run_by = 'orchestrator'`**. If **both** the item's `verify_cmd` and the verification's `command` are present they must match; a null `command` does **not** fail the guard | *"worker self-reports of 'tests pass' are the claim you would most regret trusting"* |
| 3 | no child item is open — except a run's own `backlog` phase, which the close carries out (§4) | a phase closed over unfinished tasks |
| 4 | no open `finding` with `severity = 'blocker'` targets it | gate blockers dissolved into prose |
| 5 | for kinds with `requires_commit` **set**: ≥1 `artifact` of kind `commit`. **No seeded kind sets it, `task` included** — see below | *"every accepted task ends in a commit"* — a rule the corpus itself does not keep |

> **Why condition 2 loosened, 2026-09-20.** `verification.command` became nullable (doc 02, import
> finding #1): the corpus records the *result* far more often than the command. The essential
> protection is **`run_by = 'orchestrator'`** — that a human-directed agent ran it, not the worker
> who wrote the code. Command-matching was belt-and-braces, and keeping it mandatory would have
> made the guard unsatisfiable for most real evidence, which teaches agents to reach for `force`.
> A guard that cannot be satisfied honestly is worse than a weaker one.

Conditions 1–5 are checked **together**, and the refusal lists **all** failures at once — doc 06 §6.
Three round-trips to discover three problems reads as an adversarial system and trains an agent to
reach for `force`.

### ⚠ Condition 5 is deliberately dormant, and it should stay that way

U4 flagged that **no seeded kind sets `requires_commit`, `task` included** — so condition 5 is
correct, unit-tested, and can never fire. The obvious fix is a one-row migration setting it true
for `task`, because `orchestrate` says *"every accepted task ends in a commit."*

**Do not.** The corpus does not keep that rule, and it is right not to:

```
3.4  Two-pass resort join + keep the `rid=0` placeholder — ACCEPTED (no commit: zero repo files)
3.R3 Re-ingest + measurement — ACCEPTED (commit pending; the task touched no repo file)
```

Recon, measurement, re-ingest and seeding tasks legitimately touch **nothing in the repo**.
Requiring a commit would make them unclosable, and an agent facing an unclosable task reaches for
`force` — which converts a guard into a formality and trains the habit that defeats every other
guard.

**So `requires_commit` stays per-kind configuration, off by default.** Turn it on for a kind whose
work is *always* a code change, if such a kind is ever defined. The guard exists and is tested; it
is simply not armed for `task`, on purpose.

**This is the third time real data has overruled a rule that sounded like discipline:**

| Rule | Why it could not be satisfied honestly |
|---|---|
| `verification.command` NOT NULL | the corpus records the *result* far more often than the command |
| "a phase is done when its children are" | erases the gate — a finished phase is *ready to gate* |
| "every accepted task ends in a commit" | recon and measurement tasks touch no repo file |

The shared lesson, and it is the most important one in this document: **a guard that cannot be
satisfied honestly is worse than no guard**, because it does not stop the dishonest close — it
teaches every agent the override, and the override then passes through the guards that mattered.

### `force: true`

Allowed on every condition, and **never hidden** — the refusal names it (doc 06 §6). It requires
`reason`, writes a `forced_transition` event, and `work_audit` lists that event
**permanently** (its `forced-closure` check). `work_brief` does not show it.

This is deliberate. An escape hatch that is hidden gets worked around; one that leaves a mark gets
used honestly. `controller` §8's *"do not answer it on their behalf"* has a counterpart here: an agent
genuinely blocked at 3am should be able to proceed **and leave evidence that it did**.

---

## 4. Deferral is a move, not a status

**There is no `deferred` status**, on purpose. `deferred` as a state is where work goes to disappear —
the corpus has 151 mentions of "Deferred" and a follow-up list nobody ever closed.

```
work_status(ref: "4.7", to: "deferred", target: <item ref | "backlog">, reason: "...")
```

In one transaction:

1. creates a **new open item** (`kind: followup`) under `target`, titled from the deferred item,
   carrying its `intent` and criteria, and numbered `<ref>-d`, `<ref>-d2`, … in the **target's** run
   (a target in another run numbers it there);
2. links `new --derived_from--> 4.7`;
3. closes `4.7` as `done` with `resolution = 'obsolete'`, **bypassing the §3 conditions** — which is
   sound, because the work did not vanish, it moved;
4. writes a `deferred` event naming both.

**`target` is required.** Deferring to nowhere is the failure this whole mechanism exists to prevent,
so it is not expressible.

**`target: "backlog"` is the project-level backlog run** of the deferred item's project — never a
phase inside the item's run, which would hold that run open. It is, in order: an open run under the
project whose ref is `backlog`; else the one the server made before (title `Backlog — work deferred
out of the project's runs`); else a new run, created on first use with a `run_config` (empty
`serialization_key`, so a follow-up claims the tree it is worked in — §5), and a ref no item in the
project uses yet: `backlog`, else `<project>-backlog`, `<project>-backlog2`, …. The `OK` line's `Next`
names `work_next(scope:…)` with a ref that resolves to that run alone.

**A run that still holds an in-run `backlog` phase** (written before this change) stays closable. When
`work_status(<run>, "done")` finds that the run's only open child is that phase, the same
transaction, before the §3 conditions are read:

1. moves every open item of the phase to the project-level backlog run above. Each item keeps its ref
   if it is free there, else it is numbered `<ref>2`, `<ref>3`, …. Items move by id, so links and
   addressed notes follow them;
2. closes the emptied phase as `done` with `resolution = 'obsolete'`;
3. writes a `carried` event on the run, and the `OK` line names where each item went:
   `backlog carried 1 open item(s) to <run>/: 1.1-d`.

Any other open child still fails condition 3 as before, and the refusal rolls back the carry.

> **Changed 2026-10-22 (ws/12).** `target:"backlog"` created a `backlog` phase inside the item's run,
> and that phase then refused the run's close (`1 child item(s) still open: backlog`). The controller
> had to make a project-level run and move items by hand, and named it `backlog`, which then collided
> with `m0/backlog`. "backlog" now means the project-level run, and a run left holding an in-run
> backlog is carried out on close. A cancelled in-run `backlog` phase no longer makes the deferral
> collide (ws/5's edge): the deferral does not go into the run at all.

---

## 5. Claims and leases

`in_progress` means *someone is working on this right now*, and that claim must be falsifiable.

| | |
|---|---|
| **TTL** | 30 min default, from `workspace.settings`. |
| **Heartbeat** | any write by the claim holder extends it. No separate heartbeat call — a tool that needs a keep-alive gets forgotten. |
| **Expiry** | the lease lapses; the item stays `in_progress` but the claim shows expired. |
| **Sweep** | on any `work_brief`/`work_next`/`work_status`, expired claims are released with a `claim_expired` event and the item returns to `ready`. |
| **Surfacing** | an expired claim is the **top-ranked** attention line (doc 06 §5). |

Two Postgres guarantees from U1 carry this, and both are tested in **both directions**:

- `claim_one_live_per_item` — one live claim per item;
- `claim_one_writer_per_tree` — one live **write** claim per working tree (`serialization_key`,
  scoped to its workspace, and a path to its user as well: a path names a folder on one person's
  machine, so user A's claim on `p:/projects/x` does not block user B on theirs, and still blocks A's
  other sessions. A name — a branch, the ref-scope fallback — blocks across users in the workspace; a
  path claimed by a credential with no user is the workspace's, as before m60 — doc 02 §10).

That second is the parallel-orchestrator rule: **two units in different worktrees proceed
concurrently, even in one run; two sharing a working tree do not.** Read-only claims (recon) do not
take the lock.

> This is the machine answer to `nested-claude`'s *"a parked agent is indistinguishable from a busy
> one"*. A parked agent stops writing, its lease lapses, and the store says so.

---

## 6. What U4 must test — and in both directions

The refusal tests are easy and they are only half the job. A guard that refuses *everything* passes
every one of them, so each needs its permissive twin:

| Refuses | Must still allow |
|---|---|
| `done` with an unchecked criterion | `done` once it is checked |
| `done` on worker-only verification | `done` on an orchestrator verification |
| `done` with an open child | `done` once the child closes |
| `done` with an open blocker finding | `done` once the finding is closed **or deferred** |
| `in_progress` while another writer holds the tree | `in_progress` on a different tree — another run's key, or another `tree:` in the same run |
| `ready` while a blocker is open | `ready` the moment the blocker closes |
| deferral without a `target` | deferral **with** one, which creates a real open item |

Plus, and these are the ones that catch a guard that over-corrects:

- [ ] **`force: true` succeeds and writes a `forced_transition` event that `work_audit` reports.**
      A `force` that silently succeeds is worse than no override at all.
- [ ] **The refusal message lists ALL unmet conditions**, not the first — seed an item failing three
      and assert three `→` lines.
- [ ] **Every refusal contains a callable line.** One regex over every refusal the guard can emit.
      This is doc 04 §3 as a test rather than an intention.
- [ ] **A deferred item's replacement is open, and `work_next` returns it.** Deferral that produces
      an item nobody will ever see is the original failure wearing a schema.

**Red-check each**: confirm it fails with the guard removed, restore, confirm green.
