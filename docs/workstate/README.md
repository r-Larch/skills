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
| **[04-self-explaining.md](04-self-explaining.md)** | **The acceptance bar.** The five channels that teach a coding agent to use this without a skill file — `ServerInstructions`, tool descriptions that name the next call, errors that are instructions, `work_brief` as tutorial, prompts as long form — and the three falsifiable checks that prove it. |

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

## Status

**Phase 0 closed 2026-09-18.** Source: `mcp/`. U0 landed: a five-project .NET 10 solution, compose
files, and a probe server Claude Code connects to over HTTP.

Both premises are now **measured, not assumed**:

- **Task 0.1 - the SDK**, read from the 2.2.0 assemblies (doc 01 §11). No correction needed.
- **Task 0.2 - the client**, two probe rounds against Claude Code (`mcp/PROBE.md` §5-6). It
  **corrected the design in four places**: `structuredContent` *replaces* the text block rather than
  accompanying it, so the output contract is now **text-only** (§2); server prompts are invisible, so
  the doctrine moves into `work_brief(for: ...)` (§9); completions have no client mechanism at all
  (§8); and `[Authorize]` narrows `tools/list` unaided, so `WithListToolsHandler` is dropped (§6).

Three of those four make the build *smaller*. The fourth would have made the compact rendering - and
the attention channel the whole forced-closure story leans on - **invisible to the model**, and it
was found before a line of the real server existed. That is the entire argument for probing first.
