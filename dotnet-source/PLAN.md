# dotnet-source — design plan

Status: **proposal / not yet built.** Sibling to the `dotnet-reflect` plugin in this marketplace.
Goal: ReSharper-class *source-level* navigation for a large .NET solution you are editing — search,
outline, structural map, and semantic find-usages across all projects — driven from the terminal by an
agent, greppable output, no IDE.

---

## 1. Why this exists (and why not just dotnet-reflect)

`dotnet-reflect` reads **compiled assemblies** (MetadataLoadContext + IL + decompiler). That is the right
substrate for *a dependency you don't own*. It is the **wrong** substrate for *the code you are editing*,
for four reasons that no amount of `--bin` cleverness fixes:

1. **It needs a green build.** A mega-solution mid-refactor is often not compiling — exactly when you most
   need to navigate. Roslyn parses broken source fine; `--bin` has nothing to read.
2. **Public surface only** (`GetExportedTypes`). The `private`/`internal`/local guts of a god-class — the
   stuff you're untangling — are invisible.
3. **No true source spans.** Even with PDBs you get IL sequence points, not declaration sites, comments,
   regions, or partial-class parts merged.
4. **find-usages boundary.** Declaration-position and local-variable usages never appear in IL; ReSharper's
   value is precisely those.

So the two skills are **complementary, not overlapping**, and must stay **separate plugins** with a crisp
routing boundary:

| Skill | Substrate | Answers | Precondition |
|---|---|---|---|
| `dotnet-reflect` | compiled DLLs | "shape of a dependency; who *executes* against a symbol" | a package/DLL on disk |
| **`dotnet-source`** | **source `.cs` via Roslyn** | **"navigate the solution I'm editing; every reference incl. private/local/decl"** | **source tree (no build for Tier 1)** |
| `graphify` (existing) | any input → graph | "architectural map, communities, god-nodes, relationships" | — |

`graphify` = the **map** (birds-eye relationships). `dotnet-source` = the **scalpel** (exact signatures,
exact usages, exact spans). Don't rebuild community-detection here.

**Do not merge into dotnet-reflect.** The verbs overlap (`find`/`surface`/`find-usages`) but the
implementations share ~nothing (Roslyn vs reflection). Merging muddies the model's routing trigger and
bloats one SKILL.md with two mental models. Cross-reference instead (each SKILL.md points at the other).

---

## 2. Core architectural bet: syntax-first, two tiers

The god-class scenario decomposes by **cost and precision**, and most of the value is cheap:

### Tier 1 — syntax-only (no build, no MSBuild, works on broken code, sees private)
Parse `.cs` directly with `CSharpSyntaxTree.ParseText`. No project system, no compilation, no reference
resolution. Fast, robust, stateless. Covers:
- **search** — typed symbol lookup across all projects
- **outline** — a type's/file's full member list, private included, partials merged
- **tree** — project → namespace → type structural map
- **metrics** — members/LOC per type, ranked → **automatic god-class detection**

This tier is the big lever, lowest risk, and the part `grep`+LLM does worst.

### Tier 2 — semantic (needs a Roslyn `Compilation`)
- **find-usages** (real ReSharper parity: declarations, locals, overrides, `file:line:col`)
- **impls** (interface → implementors, base → derived)
- **calls** (call hierarchy: callers/callees)
- **unused** (declared-but-never-referenced; whole-solution)

**The key decision that makes Tier 2 feasible: use `AdhocWorkspace`, NOT `MSBuildWorkspace`.**
`MSBuildWorkspace.OpenSolutionAsync` runs a design-time build of every project — slow at scale, fragile
about SDK resolution, and re-paid on *every* command (a file-based script has no persistent process).
Instead we **assemble a Roslyn `Solution` in memory ourselves**:
- discover the `.csproj` graph;
- for each project, add its parsed `.cs` as documents;
- add `MetadataReference`s pulled from the project's **`bin`** (or, if absent, from `project.assets.json`
  packages + the shared-framework dirs — reusing `dotnet-reflect`'s `FrameworkDirs` resolver);
- add `ProjectReference`s from the csproj's `<ProjectReference>` items.

Then **all of `SymbolFinder`** lights up (`FindReferencesAsync`, `FindImplementationsAsync`,
`FindDerivedClassesAsync`, `FindCallersAsync`) — cross-project correct — with **zero MSBuild**. Tier 2's
only precondition is that reference DLLs exist somewhere (a prior build, or restored packages); if they're
missing, degrade with a clear message and note that Tier 1 still works build-free.

Cross-compilation symbol identity (a symbol defined in project A, used in B) is matched by
`ISymbol.GetDocumentationCommentId()` when we resolve the CLI `<symbol>` string to a concrete `ISymbol`.

---

## 3. Command specs

All commands are file-based C# apps (`dotnet run <cmd>.cs …`), `#:include source-common.cs`, output is
**one record per line, greppable**, `file:line` clickable, columns aligned. Package:
`Microsoft.CodeAnalysis.CSharp` (Tier 1) + `Microsoft.CodeAnalysis.Workspaces.Common` (Tier 2).

### Discovery (shared)
- Target selection: `--sln <path>` | `--proj <csproj>` | `--root <dir>` | default = walk up from cwd for
  `*.slnx`/`*.sln`, else treat cwd as root.
- `.slnx` (Nomos uses `Nomos.slnx`) parses as simple XML; `.sln` classic format; fallback = glob all
  `*.csproj`. Each csproj dir roots a project; each `.cs` is assigned to its nearest ancestor csproj.
- Exclusions by default: `bin/`, `obj/`, `*.g.cs`, `*.Designer.cs`, generated `obj/**` sources.
  `--include-generated` to opt in.

### Tier 1

```
# SEARCH — typed symbol lookup across all projects (beyond grep: filter by kind, show signatures).
search.cs <pattern> [--kind class,interface,method,prop,field,enum,record] [--regex] [--sln|--root …]
#   -> kind  Ns.Type.Member(sig)   file:line   [modifiers]

# OUTLINE — the source "surface" of a type (or file): every member + signature + line, PRIVATE included,
#   partial parts merged (each part's file listed). The god-class overview.
outline.cs <TypeName|pattern> [--members] [--sln|--root …]     |     outline.cs --file <path.cs>

# TREE — project → namespace → type map across the whole solution, with per-type member counts.
tree.cs [namespaceFilter] [--sln|--root …]

# METRICS — rank types by size to surface god-classes.
metrics.cs [--top 30] [--sort members|loc|methods|params] [--sln|--root …]
#   -> LOC  Members  Methods  MaxMethodLOC  Ctors  Ns.Type   file
```

Implementation: a `CSharpSyntaxWalker` collects declarations (class/struct/record/interface/enum/delegate/
method/ctor/property/field(×N vars)/event/indexer/operator) → (kind, FQN, modifiers, syntactic signature,
file, line). FQN from ancestor walk; partials keyed by FQN. Parallel parse (`Parallel.ForEach`) over the
file set. No semantics needed — signatures are rendered from syntax text (not resolved), which is fine for
navigation.

### Tier 2 (semantic; builds the AdhocWorkspace `Solution` once per invocation)

```
# FIND-USAGES — ReSharper parity: every reference incl. declarations, locals, overrides.
find-usages.cs <symbol> [--sln|--root …]
#   <symbol> = Ns.Type.Member | Type.Member | Member (disambiguates by overload if resolvable)
#   -> grouped by project → file, `line,col  <source line>` (like the screenshot)

# IMPLS — interface implementors / base's derived classes.
impls.cs <TypeOrInterface> [--derived] [--sln|--root …]

# CALLS — call hierarchy.
calls.cs <method> [--callers|--callees] [--sln|--root …]

# UNUSED — declared but never referenced (scope to a project/type to bound cost).
unused.cs [--kind …] [--proj <csproj>|--type <Ns.Type>] [--sln|--root …]
```

Implementation: build the in-memory `Solution` (see §2), resolve `<symbol>` to an `ISymbol` (by walking
declarations and matching name/overload, or by doc-comment-ID), then delegate to `SymbolFinder`. Output
mirrors ReSharper's grouping (project → file → `line,col code`) but stays greppable.

---

## 4. Shared helper: `source-common.cs`
- **Discovery**: locate sln/root, enumerate csproj, `.cs`→project mapping, exclusion filters.
- **Parse cache**: parallel parse → `(path, SyntaxTree, projectId)`; reused by every Tier-1 command.
- **Syntax render**: FQN-from-ancestors, signature-from-syntax, modifier extraction, LOC/line spans.
- **Workspace build (Tier 2)**: assemble `AdhocWorkspace` `Solution` — projects, documents,
  bin/assets-derived `MetadataReference`s, `ProjectReference`s. Reuse dotnet-reflect's shared-framework
  resolver for framework refs.
- **Output**: column formatter, project/file grouping, `file:line` emitter.

---

## 5. Risks & mitigations
- **MSBuildWorkspace fragility/slowness** → avoided entirely via `AdhocWorkspace` + bin/assets references.
  Only fallback if reference resolution proves impractical (measure first).
- **Tier 2 needs reference DLLs** → try each project's `bin` first; else `project.assets.json` +
  shared-framework; else emit a clear "build once for semantic queries; Tier 1 works without it."
- **Scale / per-command parse cost** → parallel parse; measure on Nomos. If interactive latency bites, add
  an optional on-disk index (parsed-declaration cache keyed by file mtime) — *not* a persistent daemon.
- **Generated-file noise** → exclude `obj/`, `*.g.cs`, `*.Designer.cs` by default; `--include-generated`.
- **Roslyn vs project LangVersion** → parse with `LanguageVersion.Latest`; reading newer syntax is safe.
- **`.slnx` newness** → XML parse is trivial; `*.csproj` glob is the always-works fallback.

---

## 6. Packaging (mirrors dotnet-reflect)
```
dotnet-source/
  .claude-plugin/plugin.json            # name dotnet-source, no version (SHA updates), repo, MIT
  skills/dotnet-source/
    SKILL.md                            # trigger, escalation ladder, tier note, cross-refs, maintenance
    scripts/source-common.cs            # shared (#:include'd)
    scripts/{search,outline,tree,metrics}.cs        # Tier 1
    scripts/{find-usages,impls,calls,unused}.cs     # Tier 2
```
- Add a `dotnet-source` entry to the root `.claude-plugin/marketplace.json` `plugins` array.
- SKILL.md `description` trigger centers on: *"navigate / search / outline / find-usages across a large
  .NET **source** solution I'm editing."* Explicit cross-refs: compiled dependency → `dotnet-reflect`;
  architectural map → `graphify`. Include the same "maintaining this skill — issues/PRs" section.
- Install: `/plugin install dotnet-source@rlarch`. Requires .NET 10 SDK (file-based apps).

---

## 7. Phased rollout
1. **Phase 1 — Tier 1** (`search`, `outline`, `tree`, `metrics`), syntax-only. Prove on the Nomos solution
   (`P:\Projects\Magic\Nomos\Nomos\Nomos.slnx`). Guaranteed win, ~no risk. **Ship this first.**
2. **Phase 2 — `find-usages`** via AdhocWorkspace. The ReSharper-parity headline. Validate against the
   same `IsDevelopment` case and cross-check with dotnet-reflect's IL find-usages (should agree on
   call-sites, and source should additionally show declarations/locals).
3. **Phase 3 — `impls`, `calls`, `unused`.**
4. **Phase 4 — polish**: cross-refs both directions, optional index if latency warrants.

---

## 8. Open decisions to confirm before Phase 1
1. **Skill name**: `dotnet-source` (recommended) vs `dotnet-nav` / `dotnet-code`.
2. **Tier-2 references**: prefer project `bin` first, fall back to `assets.json` + framework (recommended)
   — accept "build once" as the semantic-tier precondition?
3. **search default**: substring (recommended) vs regex; `--regex` for the other.
4. **Output default**: flat greppable with optional `--group` (recommended) vs ReSharper-style grouped by
   default for `find-usages`.
5. Anything in the Tier-1/Tier-2 command set to add or drop for v1? (e.g. `go-to-def` is mostly covered by
   `search`; a `namespace-tree` mode; a `--json` output for all commands.)
