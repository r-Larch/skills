# WorkState.Core

Domain, EF model, migrations, query services and the renderers (`docs/workstate/03-stack-and-plan.md`
§"Solution layout"). **No ASP.NET.**

The schema is `docs/workstate/02-data-model.md`. Read that first — this is its implementation, not a
second description of it.

| | |
|---|---|
| `Domain/` | the entities, one file per group: `Tree`, `Docs`, `Evidence`, `Threads`, `Enums` |
| `Data/` | `WorkStateDbContext` + the enum/naming registration every entry point must share, the design-time factory, and `SeqAllocator` |
| `Migrations/` | every migration, in timestamp order — scaffolded with `dotnet ef migrations add <Name> --project mcp/WorkState.Core`, applied by the host or the tests, never by `dotnet ef database update` |
| `Queries/` | the hand-written read side — frontier, unclosed dependencies, rollup, subtree, open threads, search, plus the brief's constraints / notes / evidence / attention projections |
| `Rendering/` | the exact output formats of `docs/workstate/06-rendering.md`. Pure functions: no database, no web host |
| `Board/` | `BoardReader` — the composition from `Queries/` into the models `Rendering/` takes. What the eight MCP read tools are a thin transport over |

## Two things that will surprise you

**Some columns are trigger-owned.** `item.ancestors`, `item.ref_scope_id` and
`assumption.ref_scope_id` are computed by `BEFORE` triggers and mapped `ValueGeneratedOnAddOrUpdate`,
so EF never writes them and reads them back through `RETURNING`. Setting them in C# does nothing.
Reparenting an item rewrites `ancestors` for its **whole subtree** in one statement.

**The rules live in the database, not in a service.** Duplicate refs within a run, `blocks` cycles,
a resolution without `done`, two live write-claims on one working tree, an edited journal event —
all of these are refused by Postgres, with an error that names what to do instead. Do not re-implement
them in application code; catch the `PostgresException` and surface its message.

**Part of the schema is invisible to EF.** Triggers, views, and the two `DEFERRABLE` unique
constraints (`item_sibling_order`, `doc_block_seq_unique`) are raw SQL inside the migrations, because
EF cannot express them — so they are **not in the model snapshot**. Consequences, both real:

- `dotnet ef migrations add` will not see them, and can scaffold a change that silently drops or
  duplicates one. **Read the raw-SQL blocks in the existing migrations before scaffolding a change to
  `item`, `doc_block`, `event` or `claim`.**
- The snapshot is therefore *not* a complete description of the database. The complete description is
  `docs/workstate/02-data-model.md` plus the raw SQL in `Migrations/`.

**Dapper matches a record constructor by the reader's field types, so a near-miss is a hard
failure, not a conversion.** Two traps, both hit once already: a `timestamptz` comes back as a UTC
`DateTime`, so a record field typed `DateTimeOffset` will not materialise without the
`TimestampHandler` in `Queries/BoardQueries.cs`; and a native enum column comes back as the CLR
enum, so a field typed `string` throws the same way. The error names the signature it wanted —
read it, it is telling you the column types.

Migrations are generated here (`dotnet ef migrations add … --project mcp/WorkState.Core`) and applied
by the host or by the tests. Never run `dotnet ef database update`.
