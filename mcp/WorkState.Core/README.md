# WorkState.Core

Domain, EF model, migrations, query services and the renderers (`docs/workstate/03-stack-and-plan.md`
§"Solution layout"). **No ASP.NET.**

The schema is `docs/workstate/02-data-model.md`. Read that first — this is its implementation, not a
second description of it.

| | |
|---|---|
| `Domain/` | the entities, one file per group: `Tree`, `Docs`, `Evidence`, `Threads`, `Enums` |
| `Data/` | `WorkStateDbContext` + the enum/naming registration every entry point must share, the design-time factory, and `SeqAllocator` |
| `Migrations/` | four migrations: `InitialTree`, `EvidenceAndJournal`, `Threads`, `Views` |
| `Queries/` | the hand-written read side — frontier, unclosed dependencies, rollup, subtree, open threads, search |

## Two things that will surprise you

**Some columns are trigger-owned.** `item.ancestors`, `item.ref_scope_id` and
`assumption.ref_scope_id` are computed by `BEFORE` triggers and mapped `ValueGeneratedOnAddOrUpdate`,
so EF never writes them and reads them back through `RETURNING`. Setting them in C# does nothing.
Reparenting an item rewrites `ancestors` for its **whole subtree** in one statement.

**The rules live in the database, not in a service.** Duplicate refs within a run, `blocks` cycles,
a resolution without `done`, two live write-claims on one working tree, an edited journal event —
all of these are refused by Postgres, with an error that names what to do instead. Do not re-implement
them in application code; catch the `PostgresException` and surface its message.

Migrations are generated here (`dotnet ef migrations add … --project mcp/WorkState.Core`) and applied
by the host or by the tests. Never run `dotnet ef database update`.
