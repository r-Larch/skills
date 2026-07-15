---
name: nuget-api
description: >-
  Inspect the API surface of a .NET NuGet package / assembly when you don't know
  how to use it — list types & members, get exact signatures (return types,
  property types, ctors, enum values) merged with XML-doc summaries, search for a
  type/method by name, decompile a type to real C# with method bodies, or diff the
  surface between two versions. USE FOR: "how do I call this package", unknown API
  surface, wrong overload, "what's the type called", verifying a signature against
  the *installed* version, understanding a method's behavior, "what changed between
  vX and vY". Each action is ONE command that takes a package id + version and
  builds everything it needs itself. Reads the DLL on disk — more accurate than web
  docs because it's the exact version. DO NOT USE FOR: packages you already know, or
  non-.NET packages.
---

# nuget-api — analyze a .NET package's API surface

Six file-based C# scripts under `scripts/`. Each **action is one command**: give it a
**package id + version** and it builds a throwaway project referencing that package (the
"workbench"), resolves the primary assembly + XML doc, and reads it. You do **not** hunt for
DLLs, dependencies, or XML paths — the scripts do that in code. Run with `dotnet run <script>.cs …`
(needs the **.NET 10 SDK** — the scripts use `#:package`/`#:include` directives).

Version is a positional arg and may be the literal **`latest`** (newest cached, else fetched).

## The scripts

```bash
# LOCATE a name — start here when you don't know what a type/method is called.
dotnet run find.cs <pkgId> <version> <pattern>
#   e.g. find.cs OpenAI 2.12.0 Streaming   -> every Streaming* type & *Streaming* method + signatures

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
4. **Porting across versions / "did this change"?** → `diff.cs`.
5. **Just want to see what's in the cache?** → `cache.cs`.

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

## Advanced: reuse an existing bin (`--bin`)

`surface.cs`, `find.cs`, and `decompile.cs` also accept a pre-built bin folder instead of a
package id — use this to inspect an assembly you already build (e.g. a project in this repo):

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

## Maintaining this skill (IMPORTANT — do this, don't route around it)

These scripts are meant to evolve. **If a script errors, produces wrong/partial output, is slow,
or doesn't support what you need — fix it, then continue with the fixed version.** Examples:
a package that only ships `netstandard2.0` and won't resolve; a needed view the scripts don't
emit (attributes, generic constraints, nested types, interface members, `[Obsolete]` flags);
a new mode (search by return type, dump a whole namespace tree, JSON output). Prefer improving
the shared helpers (`common.cs` = cache/workbench/xml, `reflect.cs` = load + render) over
duplicating logic in an action script. Add support rather than working around a gap. After any
change, re-run the affected script once to confirm it still works before relying on the output.

Layout: `SKILL.md` + `scripts/{common,reflect}.cs` (shared, `#:include`d) +
`scripts/{find,surface,decompile,diff,cache,bindir}.cs` (actions). Reflection is load-only
(`MetadataLoadContext`) and decompilation is in-process (`ICSharpCode.Decompiler`), so nothing
executes the target package and no global tools are required.

**Cross-platform**: works on Windows, macOS, and Linux — it uses portable .NET path/temp/runtime
APIs and shells out only to `dotnet`. Paths, the NuGet cache (`$NUGET_PACKAGES` or `~/.nuget/packages`),
the temp workbench, and the shared-framework resolver all resolve per-OS. (On Linux, `--bin` assembly
names are case-sensitive — the by-package form derives the exact name for you.)

To force a clean workbench (e.g. after a failed restore): delete the workbench folder under your
temp dir — `%TEMP%\nuget-api-wb\<pkg>__<version>\` on Windows, `${TMPDIR:-/tmp}/nuget-api-wb/<pkg>__<version>/`
on macOS/Linux — and re-run.
