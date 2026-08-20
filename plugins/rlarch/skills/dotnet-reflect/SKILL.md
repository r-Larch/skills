---
name: dotnet-reflect
description: >-
  Inspect the API surface of a .NET NuGet package / assembly when you don't know
  how to use it — list types & members, get exact signatures (return types,
  property types, ctors, enum values) merged with XML-doc summaries, search for a
  type/method by name, decompile a type to real C# with method bodies, find every
  place a symbol is USED across a set of compiled assemblies (reverse "find usages",
  read from IL), or diff the surface between two versions. USE FOR: "how do I call
  this package", unknown API surface, wrong overload, "what's the type called",
  verifying a signature against the *installed* version, understanding a method's
  behavior, "who calls this / where is this used", "what changed between vX and vY".
  Each action is ONE command that takes a package id + version and
  builds everything it needs itself. Reads the DLL on disk — more accurate than web
  docs because it's the exact version. DO NOT USE FOR: packages you already know, or
  non-.NET packages.
---

# dotnet-reflect — analyze a .NET package's API surface

Seven file-based C# scripts. Each **action is one command**: give it a **package id + version** and it
builds a throwaway project referencing that package (the "workbench"), resolves the primary assembly +
XML doc, and reads it. You do **not** hunt for DLLs, dependencies, or XML paths — the scripts do that
in code. Run with `dotnet run <script>.cs …` (needs the **.NET 10 SDK** — the scripts use
`#:package`/`#:include` directives).

**Locating the scripts (do this first).** The `.cs` files live in the `scripts/` directory beside this
SKILL.md. Resolve its absolute path, then `cd` there before running the commands below:
- Installed as a **plugin**: `"$CLAUDE_PLUGIN_ROOT/skills/dotnet-reflect/scripts"` (run `echo "$CLAUDE_PLUGIN_ROOT"` to confirm it's set).
- A **local skill** or a repo checkout: the `scripts/` folder next to this file.

Version is a positional arg and may be the literal **`latest`** (newest cached, else fetched).

## The scripts

```bash
# LOCATE a name — start here when you don't know what a type/method is called.
dotnet run find.cs <pkgId> <version> <pattern>
#   e.g. find.cs OpenAI 2.12.0 Streaming   -> every Streaming* type & *Streaming* method + signatures

# FIND-USAGES — who USES a symbol, read from compiled IL (reverse "find usages", ReSharper-style).
dotnet run find-usages.cs --bin <binDir> <symbol> [--only a,b]
#   scans every method's IL across the assemblies for references to <symbol>; with a portable PDB it
#   prints file:line + the source line, else the containing method. Semantic — catches env.IsDevelopment()
#   extension-call syntax, overloads, generics. Primary form is --bin (point at your BUILT solution output).
#   e.g. find-usages.cs --bin Nomos.Web/bin/Debug/net10.0 --only Nomos IsDevelopment
#   also: find-usages.cs <pkgId> <version> <symbol>   -> usages WITHIN that package's own assemblies.
#   <symbol> = Member | Type.Member | Namespace.Type (matched at dotted-segment boundaries).

# SURFACE — exact signatures + XML-doc summaries for matching types. The default digest.
dotnet run surface.cs <pkgId> <version> [typeFilter] [--inherited]
#   e.g. surface.cs OpenAI 2.12.0 Chat.ChatClient
#   --inherited (or -i): also list base-type members, grouped under `  <BaseType>:` sections.

# DECOMPILE — real C# WITH method bodies (behavior: defaults, control flow, delegation).
dotnet run decompile.cs <pkgId> <version> [Namespace.TypeName]
#   omit the type to list every public full type name.
#   e.g. decompile.cs OpenAI 2.12.0 OpenAI.Chat.ChatClient

# DIFF — API changelog between two versions (added/removed types & members).
dotnet run diff.cs <pkgId> <v1> <v2> [typeFilter]
#   e.g. diff.cs OpenAI 2.11.0 2.12.0

# CACHE — where a package lives in the local NuGet cache, its versions, lib TFMs & files. No build.
dotnet run cache.cs <pkgId> [version]

# BINDIR — build the workbench and print its bin folder for reuse via the --bin form below.
dotnet run bindir.cs <pkgId> <version>
```

## Metapackages & multi-assembly packages

Some packages ship **no assembly of their own** — they're aggregators whose `lib/*/_._` are
placeholders (e.g. `OpenIddict.AspNetCore` → `OpenIddict.Server.AspNetCore`,
`OpenIddict.Validation.AspNetCore`, …). The scripts detect this and **expand to the real
assemblies the package exposes**, so `surface`/`find`/`decompile` all work on the aggregate.
With more than one assembly in play, `find` prefixes each hit with `Assembly!` and `surface`
groups output under `// ===== Assembly.dll =====` headers (empty ones are omitted when you
filter). A `typeFilter` is the way to keep a big metapackage's output focused.

## How to use it (escalation ladder)

1. **Don't know the name?** → `find.cs` with a substring.
2. **Know the type, need to call it?** → `surface.cs` — signatures + intent in one greppable dump.
3. **Signatures aren't enough (behavior)?** → `decompile.cs` for the real body.
4. **Who calls / where is it used?** → `find-usages.cs --bin <built-output> <symbol>`.
5. **Porting across versions / "did this change"?** → `diff.cs`.
6. **Just want to see what's in the cache?** → `cache.cs`.

The first run for a given package+version builds the workbench (a few seconds); it's cached under
the temp dir and **reused** on later calls, so repeated queries are fast.

## Output is greppable by design

One member per line, C#-ish signatures, XML `<summary>` appended as `// …`. So you can
`… | grep CompleteChat`, and `diff.cs` is just a structured diff of two surface dumps.

## Signature fidelity (what `surface` shows)

- **The header reads like a C# declaration**: `class X : Base, IFoo, IBar<T>` — the base type plus
  the type's *directly-declared* interfaces (those inherited from the base or implied by another
  listed interface are omitted, so it stays minimal). `decompile` if you need the exhaustive list.
- **Members are declared-only (own) by default** — inherited members are hidden. The base type is
  shown in the header (`class A : B`). Pass **`--inherited`** to append base members grouped under
  `  <BaseType>:` sections. `find` searches only declared members too.
- **Nullability is rendered**: nullable value types as `T?` (`HttpStatusCode?`, `Int32?`) and
  nullable reference types as `string?` — decoded from `NullableAttribute`/`NullableContextAttribute`,
  including nested generics (`Func<HttpContext,String?>?`). This is metadata-accurate to how the
  library was compiled; it degrades to the plain name if the flags look unexpected.
- **Modifiers & markers**: `static`, `virtual`, `override`, `abstract` on methods; `required` on
  properties; `[SetsRequiredMembers]` on constructors; `[Obsolete("…")]` on any member or type.

## Finding usages (who calls this) — `find-usages.cs`

Reverse lookup: given a **symbol**, find every method that references it, read from **compiled IL** — so
it's semantic, not textual. It sees `env.IsDevelopment()` extension-call syntax, the right overload, and
generic instantiations that a `grep` would miss or over-match. Point `--bin` at your **built** solution
output (e.g. `Nomos.Web/bin/Debug/net10.0`) — that folder holds every project's compiled assembly, so one
scan covers the whole solution, ReSharper-style. `--only Nomos` keeps a big bin fast and focused; scanning
all ~200 assemblies of a web app's bin still takes ~2s.

- **Output**: grouped by assembly → containing method, one usage per line. With a **portable PDB** present
  (a Debug build, or `DebugType=portable`), each line is `File.cs:line   <source line>`. Without a PDB it
  degrades to `(no PDB) -> <the referenced member>` at method granularity, and a note says so. `(method only)`
  means the assembly *had* a PDB but that IL offset carried no sequence point.
- **`<symbol>` matching** is at dotted-segment boundaries (case-insensitive): `IsDevelopment` (any member
  so named), `HostEnvironmentEnvExtensions.IsDevelopment` (that member, type given as a suffix), or a whole
  type `Microsoft.Extensions.Hosting.IHostEnvironment`. A trailing `(…)` param list or a `M:`/`T:` doc
  prefix is stripped for you.
- **What it catches for a *type* target**: IL operand positions — `new T`, casts / `is` / `as`, `typeof`,
  array creation, and generic arguments. **What it does NOT catch**: type usages that live only in a
  *signature* (a parameter, field, local, or return declared as `T`) — those aren't IL operands, so an
  IL scan can't see them. For "everywhere this type is mentioned in source", that's a `grep`/IDE job; this
  tool answers "who *executes* against it". (Member/method targets have no such gap — a call is always in IL.)

## Advanced: reuse an existing bin (`--bin`)

`surface.cs`, `find.cs`, `decompile.cs`, and `find-usages.cs` also accept a pre-built bin folder
instead of a package id — use this to inspect an assembly you already build (e.g. a project in this
repo). For `find-usages.cs` this is the **primary** form (point it at your solution's build output):

```bash
dotnet run surface.cs --bin "<bin/Debug/net10.0>" MyLib.dll <TypeFilter>
```

The folder **must contain the full dependency closure** (a normal project `bin/<cfg>/<tfm>/`
does). If a dependency is missing, the scripts don't crash — they print a `// PARTIAL RESULT`
or `// ERROR` note explaining what couldn't be resolved and telling you to use the by-package
form instead. If you see that note, prefer `surface.cs <pkgId> <version> …`.

## Private / authenticated feeds (nuget.config)

Packages on a private feed (e.g. a GitHub Packages source) need that feed's `nuget.config`.
The workbench builds in a temp dir, so NuGet's own directory walk won't find your repo's config —
the scripts locate one and pass it to restore. Resolution order:

1. **`NUGET_API_CONFIG`** env var — an explicit path to a `nuget.config`. Highest priority.
2. **Auto-detect** — the nearest `nuget.config` walking up from the directory you run the script in.

Credentials referenced as `%ENV_VAR%` in the config expand at restore time from the **inherited
environment**, so globally-defined tokens just work (nothing secret is copied). For a config like:

```xml
<add key="github" value="https://nuget.pkg.github.com/<owner>/index.json" />
<packageSourceCredentials><github>
  <add key="Username" value="%GITHUB_PACKAGES_USER%" />
  <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
</github></packageSourceCredentials>
```

set the env vars globally once, then either run the scripts from inside that repo, or set
`NUGET_API_CONFIG` to the config path globally so it works from anywhere:

```powershell
# Windows (PowerShell) — one-time, so private packages resolve wherever you run the scripts:
setx NUGET_API_CONFIG "P:\Projects\Magic\Nomos\Nomos\nuget.config"
```
```bash
# macOS / Linux — add to ~/.zshrc or ~/.bashrc:
export NUGET_API_CONFIG="$HOME/projects/Nomos/nuget.config"
```

`bindir.cs` prints which config it used. If a private package fails to restore with
"no nuget.config found", that's the fix.

## Manual workaround (only if a script can't help)

The XML doc ships in the cache at `<cacheRoot>/<pkg-lower>/<version>/lib/<tfm>/<Asm>.xml`
(cacheRoot = `$NUGET_PACKAGES` or `~/.nuget/packages`; `cache.cs` prints the exact paths). You
can `grep` it directly for summaries — but it has **no** return/property types or visibility and
lists unusable `Internal*` members, so treat it as a fallback, not the primary route.

## Maintaining this skill — bugs & feature requests

Source & issues: **https://github.com/r-Larch/skills** (plugin `dotnet-reflect`).
Open a bug report or feature request at **https://github.com/r-Larch/skills/issues**.

These scripts are meant to evolve. **If a script errors, produces wrong/partial output, is slow,
or doesn't support what you need**, do the right thing for where you're running it:

- **Running the installed plugin** (path contains `…/plugins/cache/…`): you **may edit this copy to
  unblock the task in progress** — but it is **regenerated on every update, so a cache-only edit is
  temporary and will be lost**. Therefore **every such edit MUST also produce an issue or a PR** so the
  change survives: open an issue at the URL above with the command, package, and output (attach a diff
  if you edited), or — preferred, if you have push access — push the same change to the repo as a
  commit/PR. Never leave a cache-only fix undocumented; `/plugin marketplace update rlarch` will
  otherwise overwrite it.
- **Working in a checkout of the repo**: fix it, re-run the affected script to confirm, then commit &
  push. No version bump needed — every commit is picked up as an update.

Examples worth fixing/filing: a package that only ships `netstandard2.0` and won't resolve; a view the
scripts don't emit (generic constraints, nested types, interface members, attributes beyond
`[Obsolete]`); a new mode (search by return type, dump a whole namespace tree, JSON output). Prefer
improving the shared helpers (`common.cs` = cache/workbench/xml, `reflect.cs` = load + render) over
duplicating logic in an action script — add support rather than working around a gap.

Layout: `SKILL.md` + `scripts/{common,reflect}.cs` (shared, `#:include`d) +
`scripts/{find,find-usages,surface,decompile,diff,cache,bindir}.cs` (actions). Reflection is load-only
(`MetadataLoadContext`), usage-scanning reads raw IL via in-box `System.Reflection.Metadata`, and
decompilation is in-process (`ICSharpCode.Decompiler`), so nothing executes the target package and no
global tools are required.

**Cross-platform**: works on Windows, macOS, and Linux — it uses portable .NET path/temp/runtime
APIs and shells out only to `dotnet`. Paths, the NuGet cache (`$NUGET_PACKAGES` or `~/.nuget/packages`),
the temp workbench, and the shared-framework resolver all resolve per-OS. (On Linux, `--bin` assembly
names are case-sensitive — the by-package form derives the exact name for you.)

To force a clean workbench (e.g. after a failed restore): delete the workbench folder under your
temp dir — `%TEMP%\dotnet-reflect-wb\<pkg>__<version>\` on Windows, `${TMPDIR:-/tmp}/dotnet-reflect-wb/<pkg>__<version>/`
on macOS/Linux — and re-run.
