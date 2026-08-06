# dotnet-source — design & decisions

Status: **built and shipped.** See `skills/dotnet-source/SKILL.md` for usage.
This file records *why* it is shaped the way it is, including what the original proposal got wrong.
It is a design record, not a roadmap.

---

## 1. Why this exists (and why not just dotnet-reflect)

`dotnet-reflect` reads **compiled assemblies** (MetadataLoadContext + IL + decompiler). That is the
right substrate for *a dependency you don't own*. It is the **wrong** substrate for *the code you are
editing*:

1. **It needs a green build.** A mega-solution mid-refactor often isn't compiling — exactly when you
   most need to navigate. Roslyn parses broken source fine.
2. **Public surface only** (`GetExportedTypes`). The `private`/`internal` guts of the god-class you're
   untangling are invisible. *(Measured: `outline AknPersistenceService` lists 30 members, 19 of them
   private. dotnet-reflect can show 3.)*
3. **No true source spans** — IL sequence points, not declaration sites, regions, or merged partials.
4. **find-usages boundary.** Declaration-position and local-variable usages never appear in IL.

The two skills are **complementary and stay separate plugins**:

| Skill | Substrate | Answers | Precondition |
|---|---|---|---|
| `dotnet-reflect` | compiled DLLs | shape of a dependency; who *executes* against a symbol | a package/DLL on disk |
| **`dotnet-source`** | **source `.cs` via Roslyn** | navigate the solution I'm editing; every reference incl. private/local/decl | source tree (Tier 1: nothing; Tier 2: a restore) |
| `graphify` | any input → graph | architectural map, communities, relationships | — |

**Cross-validated, not assumed:** on Nomos both tools independently find the **same 62 call-sites**
for `WhereTenantRead`; dotnet-source additionally reports the declaration. Two unrelated
implementations agreeing exactly is the strongest evidence available that either is right.

## 2. Two tiers

- **Tier 1 — syntax only** (`search`, `outline`, `tree`, `metrics`). `CSharpSyntaxTree.ParseText`,
  no project system, no references. Works on broken code, sees private. The big lever, lowest risk.
- **Tier 2 — semantic** (`find-usages`, `impls`, `calls`, `unused`). Needs a Roslyn `Compilation`.

**`AdhocWorkspace`, not `MSBuildWorkspace`** — no design-time build per project. The `Solution` is
assembled in memory, then all of `SymbolFinder` lights up, cross-project correct, with zero MSBuild.

## 3. What changed from the original proposal

### Compiled binary, not file-based apps *(the reason for this revision)*

The proposal used `dotnet run search.cs` per command. Now: **one compiled multi-command binary**
(`tool/DotnetSource.csproj`), built once by `ds.ps1`/`ds.sh` into a hash-keyed cache.

Be precise about *why*, because it is **not** build-caching — `dotnet run app.cs` already
content-hash-caches its own build. The reasons are:
- one process can share a parse pass across a command's whole run;
- only a compiled, long-lived process can hold a Roslyn `Solution` in memory — i.e. `serve` is
  impossible in the file-based model;
- startup is ~100 ms, which matters for an interactive tool referencing Roslyn.

Cache key = tool sources + csproj (which pins Roslyn) + **installed runtime band** (a
framework-dependent binary is tied to its runtime major). Publish is guarded by a named mutex,
staged to a temp dir and atomically moved, and gated on a `.ok` sentinel written *last* — a
half-finished publish leaves a directory that looks built, and we'd exec a broken binary forever.
*(Verified: 4 concurrent cold starts → 1 cache dir, 0 temp leftovers; corrupt dll → rebuild.)*

### Keeping the compilation alive — two layers, because the tiers differ

- **Tier 1: an on-disk parse index** keyed by `path + mtime + size` → declaration records (never
  SyntaxTrees — not serializable, and Tier 1 doesn't need them). Keyed *also* by the exclusion
  profile and by the **tool's MVID**, so a rebuilt tool can't serve an index built by older
  extraction logic. Warm `search` on Nomos: ~250 ms.
- **Tier 2: the `serve` daemon.** No on-disk cache can hold a `Compilation`; assembling one costs
  ~15 s on Nomos. The daemon holds the `Solution` resident.
  **`find-usages`: 15,200 ms → 170 ms (~90×).**
  Opt-in: commands probe and fall back to stateless, and never auto-spawn (that turns a burst of
  parallel agent commands into a herd of Solution builds). Keyed by root **and tool MVID**.
  `FileSystemWatcher.Error` triggers a full rebuild — the OS drops events on buffer overflow (bulk
  `git checkout`), and ignoring that means silently serving a stale Solution.

The original plan explicitly rejected a daemon ("*not* a persistent daemon"). Measurement changed
the answer: 15 s per semantic query is too slow to leave alone, and the index provably cannot fix it.

## 4. Correctness rules learned the hard way

Each of these silently produces *wrong* answers. All were found by spiking before writing commands.

1. **The solution file is authoritative.** Nomos has **95 `.csproj` on disk but 21 in `Nomos.slnx`** —
   73 are full solution copies under `.claude/worktrees/`. The proposal's "fallback = glob all
   `*.csproj`" would have ingested the codebase **4.5×**: duplicate FQNs, garbage metrics, duplicated
   usages, 4.5× the parse cost. Glob is last-resort only, and always with hard directory exclusions.
2. **References come from `obj/project.assets.json`, never `bin/`.** If project P is in the workspace
   as source *and* `P/bin/P.dll` is a MetadataReference, every type in P is declared twice → ambiguous
   symbols. assets.json lists only *package* paths and needs only a **restore**, never a compile.
3. **Implicit usings must be supplied.** `AdhocWorkspace` has none. Without them `Task<>`, `List<>`,
   `CancellationToken` don't bind: **10,392 CS0246** on Nomos, which would have made find-usages
   silently miss references. Prefer the real generated `obj/**/*.GlobalUsings.g.cs` — it captures
   *package-contributed* usings (`Xunit`, `Sentry`) that a csproj-only synthesis cannot know — and
   fall back to synthesising the SDK set for a never-built project.
4. **ProjectReferences must be transitive.** MSBuild flows them to the compiler; Roslyn's
   `ProjectReference` does not. `Nomos.Web` → `Nomos.Core` → `LarchSys.Ai`, and Web uses
   `LarchSys.Ai.Citation` without referencing it directly. Direct-refs-only left **317 CS0246**, all
   of them Nomos's own types.
5. **Preprocessor symbols matter.** Without `DEBUG;TRACE` + `<DefineConstants>` in `ParseOptions`,
   every reference inside `#if DEBUG` is invisible. *(Regression-tested.)*
6. **`GetDeclaredSymbol` returns null for `FieldDeclarationSyntax`** (`int a, b;` is one node, two
   symbols) — ask the `VariableDeclaratorSyntax`. Asking the wrong node doesn't error, it just
   silently never reports fields.

**Residual, accepted:** `.razor` components. Razor's source generator runs in-memory and emits no
`.cs`, so Blazor component *types* don't resolve. On Nomos that's 18 CS0246 from 4 components, with
20 of 21 projects fully clean. `discover --semantic` reports it explicitly rather than leaving a
mystery. Fixing it means hosting the Razor generator — a large, fragile detour for a bounded loss.

## 5. Packaging

```
dotnet-source/
  .claude-plugin/plugin.json            # no version (SHA updates), repo, MIT
  skills/dotnet-source/
    SKILL.md
    ds.ps1 / ds.sh                      # bootstrap launchers (hash → publish once → exec)
    tool/                               # one compiled console app, net10.0, Roslyn 5.6.0 pinned
      Program.cs Args.cs Common.cs      # dispatch, arg parsing, shared helpers
      Discovery.cs                      # slnx/sln parse, project set, .cs→project, exclusions
      Decls.cs Index.cs Tier1.cs        # syntax walker, parse index, Tier-1 commands
      Workspace.cs Symbols.cs Tier2.cs  # Solution assembly, symbol resolution, Tier-2 commands
      Server.cs                         # the serve daemon
```

Reused from `dotnet-reflect`: the `FrameworkDirs()` shared-framework resolver and the
`Cache`/`CompareVer`/`Proc` helpers, so both skills resolve the NuGet cache identically.

Roslyn is **pinned** (5.6.0): an older Roslyn parses C# 14 / net10 syntax as incomplete nodes and
silently drops declarations.

## 6. Known gaps / worth doing next

- `--json` output for every command.
- Honour explicit `<Compile Include/Remove>` (`EnableDefaultCompileItems=false`) and linked files;
  today files are assigned to their nearest ancestor `.csproj`.
- Host the Razor source generator so component types resolve (see §4 residual).
- `metrics` ranks by member count, so DTO/property-bags dominate `--sort members`; a real complexity
  measure (branching, fan-out) would beat counting.
- `unused` is O(declarations × FindReferences) — scope it with `--proj`/`--type`.
