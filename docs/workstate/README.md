# WorkState — a ticketing system for coding agents

Design for an MCP server + Postgres store that replaces the markdown bookkeeping in
`orchestrate`, `controller` and `nested-claude` with a queryable work-state store: projects →
features → plans → phases → tasks → sub-tasks, with hierarchy, dependencies, evidence and forced
closure.

Built for **coding agents first**. The human web frontend comes last and is shaped by the same
queries the agents use.

| Doc | Contents |
|---|---|
| **[00-analysis.md](00-analysis.md)** | What the skills actually persist, measured over 181 files / 55,523 lines of real runs. The fifteen kinds of knowledge conflated into two files, the unenforced schema the skills already impose, and a tool-by-tool analysis of how an agent reads and patches a 6,000-line `LEDGER.md` — which is what the MCP has to beat. |
| **[01-mcp-surface.md](01-mcp-surface.md)** | The MCP design against revision **2026-07-28**: the output contract (compact text + `structuredContent` + an attention channel), 20 tools, the `work_status` guard that makes closure mandatory, `work_patch` vs `Edit`, resources, prompts that absorb the skills' brief templates, scoped tokens that make "workers never commit" structural, and what is deliberately not used. |
| **[02-data-model.md](02-data-model.md)** | Full Postgres DDL: one recursive `item` table + a dependency DAG, block-addressed documents, attempts/reports/verifications, assumptions as a state machine, constraints with routes, notes as an outbox addressed to future briefs, claims with leases, and an append-only journal. Ends with the table of corpus failures the schema makes impossible. |
| **[03-stack-and-plan.md](03-stack-and-plan.md)** | .NET 10 + `ModelContextProtocol.AspNetCore` 2.2.0 + Postgres 17, laid out per the `nomos-service` blueprint; the SolidJS/Vite/Tailwind frontend stack (design deferred); a six-phase build plan; decisions with reversal costs; what the owner settled. |
| **[05-server-instructions.md](05-server-instructions.md)** | **The exact 40-line text that lands in every session's system prompt** — measured to arrive intact, so it is the only channel that works before the agent has called anything. The loop, the closure contract, the attention block, and the role statement the probe proved necessary. With the per-block rationale and how it gets tested. |
| **[06-rendering.md](06-rendering.md)** | **The exact output formats.** Since the contract is text-only, the renderer *is* the product: the item row and its flag vocabulary, `work_tree`, the `work_brief` recovery payload, the attention block, and the refusal format — each with the rule that makes it work and the test that keeps it honest, including a token budget that fails the build if it regresses. |
| **[07-guards.md](07-guards.md)** | **Forced closure — the product's whole claim.** The transition table, the five conditions for `done`, deferral as a *move* that creates a real item rather than a status work disappears into, claim leases and the one-writer-per-worktree rule, and the `force` override that is never hidden because a hidden escape hatch gets worked around. Ends with the permissive twin of every refusal test, because a guard that refuses everything passes all of them. |
| **[08-distribution.md](08-distribution.md)** | **How it ships and who owns the process.** Why the HTTP transport is what makes an always-on frontend possible at all, why the marketplace can only distribute a *pointer*, why the skills should take a **hard** dependency rather than dual-mode, and the one-process/two-audiences shape that serves agents and humans from the same host. |
| **[04-self-explaining.md](04-self-explaining.md)** | **The acceptance bar.** The five channels that teach a coding agent to use this without a skill file — `ServerInstructions`, tool descriptions that name the next call, errors that are instructions, `work_brief` as tutorial, prompts as long form — and the three falsifiable checks that prove it. |
| [09-dashboard-design.md](09-dashboard-design.md) | The first dashboard design. **Superseded by 13.** |
| **[10-read-api.md](10-read-api.md)** | **The JSON read API v1**, frozen and additive only: ids not refs in URLs, the `ItemRef` shape, auth by cookie, agent token or reader token, and every endpoint the dashboard is built from. |
| **[11-sessions.md](11-sessions.md)** | **Sessions as first-class records.** The role comes from the token; the session's name comes from the `X-WorkState-Agent` registration header and labels claims and events. Header-only, because the transport is stateless. |
| [12-install-harness-identity.md](12-install-harness-identity.md) | Install, harness and identity from a fresh PC: what Claude Code's MCP support measurably does, where configuration lives, and the open questions. Its credential-helper design (option G) is a **proposal**, not built. |
| **[13-dashboard.md](13-dashboard.md)** | **The dashboard as built** on the v1 read API: pages and URLs, the detail panel (reported vs verified), the state vocabulary, theme tokens. |

### Records

| File | Contents |
|---|---|
| [ACCEPTANCE.md](ACCEPTANCE.md) | The cold-start acceptance runbook and its results. |
| [PROBE.md](PROBE.md) | What Claude Code measurably surfaces of an MCP server — the evidence several design decisions above rest on. |
| [IMPORT-REPORT.md](IMPORT-REPORT.md) | The import of the real `PLAN.md` / `LEDGER.md` corpus, generated by `mcp/tools/WorkState.Import --report`. |

## The one-paragraph case

Across 25 real orchestration runs, **17 have plan files showing zero completed tasks while their
ledger declares the run finished**. 96 supersession markers, 129 corrections and 1,241 ⚠ characters
exist because markdown cannot retract, address, or nag. The skills' own plan audit spends 60–150k
tokens per run re-checking that task numbers are unique and that dependencies exist — two things a
unique constraint and a foreign key enforce for free. State that is a side-effect of narration is
never maintained; state that is the primary write is. Move the state, keep the judgment.

## Settled

- **One global deployment**, single user. A `project` item per repo; **many orchestrators run in
  parallel**, each owning a `run` subtree. Refs are scoped per project/run so `2.3` stays short and
  cannot collide; the write lock is keyed on the worktree, so separate worktrees run concurrently.
- **Artifacts are pointers** — paths, shas and URLs; bytes stay on disk.
- **Goal:** a working MCP that is self-explaining to a coding agent — see doc 04 for the bar.

## Status — built, measured, and not pushed

**Complete.** 47 commits on `feat/workstate`, 144 files, +32,486 lines. **213 tests, 0 warnings.**
Nothing is pushed; that is the owner's call.

### The claim, measured on the real artifact

| | |
|---|---|
| `entity-store/LEDGER.md` | 6,011 lines · 473,988 chars · **~118,500 tokens** |
| `work_brief` on that same run | 35 lines · **581 tokens** |
| | **204× smaller**, and generated, so it cannot go stale |

### The goal — *self-explaining to a coding agent* — tested, not asserted

Three cold-start runs from a neutral tree, prompts pre-registered **before the surface existed**.

- **`work_brief` was the unprompted first call in all three runs.** No skill, no tool name in the
  prompt, no ordering hint.
- **Zero uncorrected wrong calls** in every run.
- **Check 2 passed:** a fresh agent hit the closure refusal, saw `force` spelled out one argument
  away, and **went to run the verification instead.**
- **Run B passed by refusing.** Given a prompt naming no work, it reasoned from the
  `verify_cmd`-must-be-able-to-fail rule it had just read to the conclusion that inventing a subject
  would make its own report a lie. **Nobody wrote that conclusion down.**

### What the acceptance found that 200 tests did not

Four surfaces that **lied while passing their own unit tests** — a resource reporting an empty store
about an item that exists, a refusal naming the wrong run, a task rendered as a run, a composed URI
404ing. All four fixed, and **shown fixed in a second transcript**, not in a test name.

That is the case for the method: *defects in what a system **says** are invisible to tests of what
it **computes**, and the only instrument that finds them is an agent reading the output with no idea
what it was supposed to say.*

### Deliberately not built

`work_patch` (needs its read half — block ids rendered inline — as one slice) · the SolidJS operator
dashboard (stack fixed in doc 03 §1) · OpenTelemetry, Sentry, Scalar · rewriting the
`orchestrate` / `controller` / `nested-claude` skills against the store.

