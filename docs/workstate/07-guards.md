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
| `ready`/`open` | `in_progress` | **acquires a claim** (§5). Refused if another live claim holds this item, or a write-claim holds this `serialization_key`. |
| `in_progress` | `blocked` | requires `reason`. Records a `blocked` event. |
| `in_progress` | `review` | free. |
| `review`/`in_progress` | `done` | **the five conditions, §3.** |
| `review`/`in_progress` | `in_progress` | rework. Records the attempt count; **a fresh worker, not the old one** — that is orchestration doctrine, not a schema rule. |
| any non-terminal | `cancelled` | requires `reason`. `destructiveHint: true`. |
| any | `superseded` | **not directly.** `work_link(supersedes)` sets it in the same transaction (U1 trigger). |
| any non-terminal | `deferred` | **a move, §4.** |

**Releasing the claim** happens automatically on leaving `in_progress` by any route.

---

## 3. The five conditions for `done`

Refused unless **all** hold. Each maps to a rule the corpus shows being broken:

| # | Condition | The corpus failure it prevents |
|---|---|---|
| 1 | every `item_criterion` is `checked` | 424 unchecked boxes in "finished" runs |
| 2 | ≥1 `verification` with `passed = true`, `command` matching `verify_cmd`, and **`run_by = 'orchestrator'`** | *"worker self-reports of 'tests pass' are the claim you would most regret trusting"* |
| 3 | no child item is open | a phase closed over unfinished tasks |
| 4 | no open `finding` with `severity = 'blocker'` targets it | gate blockers dissolved into prose |
| 5 | for kinds with `requires_commit`: ≥1 `artifact` of kind `commit` | *"every accepted task ends in a commit"* |

Conditions 1–5 are checked **together**, and the refusal lists **all** failures at once — doc 06 §6.
Three round-trips to discover three problems reads as an adversarial system and trains an agent to
reach for `force`.

### `force: true`

Allowed on every condition, and **never hidden** — the refusal names it (doc 06 §6). It requires
`reason`, writes a `forced_transition` event, and that event is **permanently visible** in
`work_brief` and in `work_audit`.

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
   carrying its `intent` and criteria;
2. links `new --derived_from--> 4.7`;
3. closes `4.7` as `done` with `resolution = 'obsolete'`, **bypassing the §3 conditions** — which is
   sound, because the work did not vanish, it moved;
4. writes a `deferred` event naming both.

**`target` is required.** Deferring to nowhere is the failure this whole mechanism exists to prevent,
so it is not expressible.

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
- `claim_one_writer_per_tree` — one live **write** claim per `serialization_key`.

That second is the parallel-orchestrator rule: **two runs in different worktrees proceed
concurrently; two sharing a working tree do not.** Read-only claims (recon) do not take the lock.

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
| `in_progress` while another writer holds the tree | `in_progress` on a different `serialization_key` |
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
