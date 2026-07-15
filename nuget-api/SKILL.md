---
name: nuget-api
description: >-
  Inspect the API surface of a .NET NuGet package / assembly when you don't know
  how to use it — list types & members, get exact signatures (return types,
  property types, ctors, enum values) merged with XML-doc summaries, search for a
  type/method by name, or decompile a type to real C# with method bodies. USE FOR:
  "how do I call this package", unknown API surface, wrong overload, "what's the
  type called", verifying a signature against the *installed* version, diffing the
  surface across versions, understanding a method's behavior. Works offline against
  the restored DLL — more accurate than web docs because it reads the exact version
  on disk. DO NOT USE FOR: packages you already know, or non-.NET packages.
---

# nuget-api — analyze a .NET package's API surface

Three file-based C# scripts under `scripts/` read the **restored DLL on disk** (the exact
installed version) instead of guessing from web docs. Run them with `dotnet run <script>.cs …`
(requires the .NET 10 SDK — the scripts use `#:package` directives).

## The escalation ladder (cheapest first)

1. **grep the XML doc** — free, no build. Intent, params, exceptions, but *not* return/property types.
2. **`find.cs`** — locate a type/member by name when you don't know what it's called.
3. **`surface.cs`** — exact signatures + XML summaries for a type or namespace. **The default digest.**
4. **`decompile.cs`** — real C# *with method bodies* when signatures aren't enough (defaults, control flow, how an overload delegates).

## Step 0 — locate the two inputs (do this first)

Everything needs a **binDir**: a folder holding the target DLL **plus its full transitive
dependency closure**. Load-only reflection and the decompiler both fail on a bare cache DLL
(e.g. `FileNotFoundException: System.ClientModel`) — so point them at a built project's output,
which already has every dependency copied next to it.

```bash
ASM=OpenAI.dll            # the assembly file name (usually PackageId + .dll)
# a) find a bin folder that already contains the full closure (prefer newest TFM):
find . -path "*bin*" -name "$ASM" 2>/dev/null | grep -iE "net[0-9]" | sort | tail -1
#    -> use its DIRECTORY as <binDir>. If none exists, `dotnet build` any project
#       that references the package, then re-run.
```

The **XML doc** ships in the NuGet cache next to the cached DLL (bin output often omits it):

```bash
# cache root: $NUGET_PACKAGES if set, else ~/.nuget/packages
XML="$NUGET_PACKAGES/openai/2.12.0/lib/net10.0/OpenAI.xml"     # <pkg-lowercase>/<version>/lib/<tfm>/<Asm>.xml
grep -A3 'name="M:OpenAI.Chat.ChatClient.CompleteChat' "$XML"  # step-1 quick doc lookup
```

## The scripts

Run from `scripts/`. `<binDir>` is the folder from Step 0; `<Assembly.dll>` is resolved inside it.

```bash
# 2) FIND — case-insensitive substring across all type & member names
dotnet run find.cs <binDir> <Assembly.dll> <pattern>
#    e.g. …/net10.0 OpenAI.dll Streaming   -> every Streaming* type & *Streaming* method

# 3) SURFACE — signatures + XML summaries (public members only, Internal* filtered)
dotnet run surface.cs <binDir> <Assembly.dll> [typeFilter] [--xml <path>]
#    typeFilter = substring on type FullName; pass --xml to merge the cache doc.
#    e.g. …/net10.0 OpenAI.dll Chat.ChatClient --xml …/OpenAI.xml

# 4) DECOMPILE — full C# with bodies (in-process; no ilspycmd needed)
dotnet run decompile.cs <binDir> <Assembly.dll> [Namespace.TypeName]
#    omit the type to list every public full type name in the assembly.
#    e.g. …/net10.0 OpenAI.dll OpenAI.Chat.ChatClient
```

## Notes & gotchas

- **binDir must have the closure.** This is the #1 failure mode. Always use a `bin/<cfg>/<tfm>/`
  folder, never the raw `…/lib/<tfm>/` cache dir (that one has only the package's own DLL).
- **Pick the TFM the consumer targets** (e.g. `net10.0` here) so you see the right conditional API.
- **XML summaries are keyed by member name**, so all overloads of a method share one summary line
  (fine in practice). Return/property types and visibility come from reflection, which is exact.
- **Greppable by design** — one member per line. Diff two versions to see what changed:
  `dotnet run surface.cs <bin_v1> X.dll > a; dotnet run surface.cs <bin_v2> X.dll > b; diff a b`.
- These read metadata **without executing** the assembly (`MetadataLoadContext` / decompiler), so
  they're safe to run against any restored package.
