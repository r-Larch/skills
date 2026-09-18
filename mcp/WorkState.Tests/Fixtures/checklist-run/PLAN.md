# Import fixture — a plan whose tasks are checkboxes

> Authored for the suite in the shape RECON-CORPUS §5 measured: the tasks are checkbox lines carrying
> `Files:` / `Done:` / `Verify:` clauses, and the box is the status. 84.5% of the corpus's boxes are
> unchecked, so this fixture is mostly unchecked too.

## Goal

Prove the checklist form of a plan reaches the item tree with its criteria and its verify command.

## Phases

### Phase 1 — The store exists

- [ ] **1.1 A dev database and a test configuration** *(added after recon)* — stand up a throwaway
      database and point the test configuration at it, with a guard that fails loudly if the resolved
      host is the production one.
      Files: `Tests/Startup.cs`, compose file · Done: the suite runs against localhost and a deliberate
      production host fails the guard · Verify: `dotnet test Tests/Tests.csproj --filter FullyQualifiedName~Guard`
- [ ] **1.2 Entities and configuration** — the entities of §3 with their configurations and indexes. No
      migration yet.
      Files: `Core/Data/**` · Done: builds, no existing entity modified · Verify: `dotnet build All.slnx`
- [x] **1.3 Migration** — generate the migration and apply it to the dev database only.
      Done: the generated SQL contains only `CREATE` · Verify: `dotnet ef migrations script`

### Phase 2 — Done when

- [ ] the frontier answers with the first open task
- [ ] every imported file has its blocks verbatim
