# Import fixture — a phase closes on its gate, not on its children

> FINDINGS-live F-L1. Three phases in one run, and they are deliberately different: one whose ledger
> records a gate outcome, one whose tasks are all finished with no gate line anywhere, and one still
> holding an open task.

## Goal

Prove a phase imports `done` only where the corpus recorded a gate, `review` where the work is
finished and the gate is unrecorded, and `open` while anything under it is still open.

## Phases

### Phase 1 — The store exists

- [x] **1.1 Schema** — the tables of the spec with their configurations and indexes.
      Done: builds, nothing existing modified · Verify: `dotnet test --filter Schema`
- [x] **1.2 Migration** — generate the migration and apply it to the dev database only.
      Done: the generated SQL contains only `CREATE` · Verify: `dotnet test --filter Migration`

### Phase 2 — Ingestion

- [x] **2.1 Reader** — read the source into records.
      Done: the reader round-trips the sample · Verify: `dotnet test --filter Reader`
- [x] **2.2 Writer** — write the records, delete-then-insert per run.
      Done: row counts asserted · Verify: `dotnet test --filter Writer`
- [x] **2.3 Projector** — project entities and commit hashes after PROJECT returns.
      Done: an unchanged re-run buys zero embeddings · Verify: `dotnet test --filter Projector`

### Phase 3 — Query surface

- [x] **3.1 Query service** — the read path over the new store.
      Done: the acceptance questions answer from the store · Verify: `dotnet test --filter Query`
- [ ] **3.2 Admin API** — the operator endpoints over the same service.
      Done: every endpoint is tenant-scoped · Verify: `dotnet test --filter Admin`
