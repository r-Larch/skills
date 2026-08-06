---
name: dotnet-source
description: >-
  Navigate the .NET SOURCE solution you are editing, ReSharper-style, from the
  terminal — search for a type/member by name and kind, outline a type's full
  member list INCLUDING private/internal members with partial parts merged, map
  project → namespace → type, rank types by size to find god-classes, and answer
  "who uses this" semantically (find-usages with declarations, locals and
  overrides), "who implements this", "who calls this", and "what is never used".
  Reads .cs with Roslyn, so it works on a solution that DOESN'T COMPILE and sees
  the private guts a compiled-metadata reader cannot. USE FOR: "where is X used /
  who calls X" in my own code, "what's in this class", "outline this god-class",
  "what implements this interface", "is this dead code", "map this solution",
  "find the biggest classes", navigating an unfamiliar or mid-refactor codebase.
  DO NOT USE FOR: a NuGet package or framework symbol you don't own (use
  dotnet-reflect — it reads the compiled DLL), or a birds-eye architectural map
  with communities and relationships (use graphify).
---

# dotnet-source — source-level navigation for the solution you're editing

One compiled binary, git-style subcommands. It parses your `.cs` with **Roslyn**, so:

- **No build required** for Tier 1 — it works mid-refactor, on code that doesn't compile.
- **It sees `private`/`internal`** — the guts of the god-class you're untangling.
- **Real source spans** — `file:line`, partial parts merged, declarations included.

**Locating the tool (do this first).** Resolve the skill directory, then call the launcher for
your OS. Everything below is written as `ds <command>`; substitute accordingly.

- Installed as a **plugin**: `"$CLAUDE_PLUGIN_ROOT/skills/dotnet-source"` (run `echo "$CLAUDE_PLUGIN_ROOT"` to confirm).
- A **local skill** or repo checkout: the folder containing this SKILL.md.

```bash
# Windows (PowerShell)          # macOS / Linux
./ds.ps1 <command> …            ./ds.sh <command> …
```

The **first** call builds the tool once (a few seconds) into a hash-keyed cache and every later
call reuses the compiled binary (**~100 ms startup**). Needs the **.NET 10 SDK**.

## The commands

```bash
# ---- Tier 1: syntax only. No build. Sees private. Works on broken code. -------------------

# SEARCH — find a declaration by name. Beyond grep: filter by kind, get the signature.
ds search <pattern> [--kind class,interface,method,prop,field,enum,record,type] [--regex]
#   e.g. ds search Tenant --kind interface   -> every I*Tenant* interface + file:line

# OUTLINE — a type's complete member list, PRIVATE INCLUDED, partial parts merged.
#   The god-class overview. This is the one dotnet-reflect structurally cannot give you.
ds outline <TypeName|pattern>
ds outline --file <path.cs>                 # everything declared in one file, in line order
#   e.g. ds outline AknPersistenceService   -> 30 members, 19 of them private

# TREE — project → namespace → type, with per-type member counts.
ds tree [namespaceFilter]

# METRICS — rank types by size. Automatic god-class detection.
ds metrics [--top 30] [--sort members|loc|methods|params]
#   --sort members ranks DTOs/property-bags to the top; --sort methods or loc is usually
#   what you want when hunting god-classes.

# ---- Tier 2: semantic. Needs `dotnet restore` once — NOT a build. ------------------------

# FIND-USAGES — every reference: call-sites AND declarations, locals, overrides, across projects.
ds find-usages <symbol>
#   <symbol> = Member | Type.Member | Ns.Type.Member | Ns.Type  (matched at dotted boundaries)
#   e.g. ds find-usages WhereTenantRead     -> 62 call-sites + the declaration

# IMPLS — who implements this interface / derives from this base.
ds impls <TypeOrInterface> [--derived]

# CALLS — call hierarchy.
ds calls <method> [--callers|--callees]

# UNUSED — declared but never referenced. Scope it: this is the slow one.
ds unused [--kind method,prop,field,type] [--proj <csproj>|--type <Ns.Type>] [--include-public]
#   Non-public only by default (a public member may be called from outside the solution).
#   --include-public includes them — right for a leaf app, misleading for a library.
#   --proj/--type scope only WHAT IS REPORTED; usages are always searched solution-wide.

# ---- Keep-alive (optional, Tier 2 only) -------------------------------------------------

# SERVE — starts in the background and RETURNS once warm. Do not background it yourself.
ds serve --sln <path>     # find-usages: ~15s -> ~0.2s for every later query
ds serve --stop           # stop it        ds serve --foreground   # block instead (debugging)
ds status                 # is a daemon serving this solution? (pid, log)

# ---- Utility ----------------------------------------------------------------------------

ds discover [--semantic]  # what the tool actually sees. START HERE if any count looks wrong.
ds version
```

**Target selection — every command, `serve` and `status` included:**

| flag | meaning |
|---|---|
| `--sln <path>` | the solution to work on. **Prefer this.** It's authoritative, and for `serve` it's what *identifies* the daemon. |
| `--root <dir>` | no solution file: scan this directory tree |
| `--proj <csproj>` | just this one project — the workspace becomes a **single project**, so Tier-2 commands can't see references from sibling projects. (`unused` is the exception: there it only filters what's reported.) |

Default: walk up from cwd for `*.slnx`/`*.sln`, else treat cwd as root — so running from anywhere
inside the tree finds the same solution, and therefore the same daemon.
`--include-generated` also scans `*.g.cs` / `*.Designer.cs` (off by default).

## How to use it (escalation ladder)

1. **Don't know what it's called?** → `search <substring>` (add `--kind` to cut noise).
2. **Want the shape of a class?** → `outline <Type>` — private members and all partial parts.
3. **Where is this used / who calls it?** → `find-usages <symbol>`.
4. **Who implements / derives?** → `impls <Interface>`.
5. **Untangling a god-class?** → `metrics --sort methods` to find it, `outline` to see it,
   `find-usages` on each member to see what actually depends on it.
6. **Is this dead?** → `unused --type <Ns.Type>`.
7. **Counts look wrong?** → `discover` (and `discover --semantic` for reference health).

## Which skill? (routing)

| You want | Use |
|---|---|
| the code **you're editing** — private members, exact usages, works unbuilt | **dotnet-source** (this) |
| a **NuGet package / framework** symbol you don't own | **dotnet-reflect** — reads the compiled DLL |
| a **birds-eye architectural map**, communities, relationships | **graphify** |

`find-usages IsDevelopment` returns nothing here **by design**: `IsDevelopment` is declared in
ASP.NET, not in your source. This tool finds what's declared in *your* solution. For a symbol you
don't own, use `dotnet-reflect`, which scans compiled IL. (The two agree exactly where they
overlap — validated on Nomos: both find the same 62 call-sites for `WhereTenantRead`; this tool
additionally shows the declaration, which IL structurally cannot.)

## Keeping the compilation alive

Two layers, because the two tiers have different costs:

- **The tool binary** is compiled once into `$TEMP/dotnet-source/tool/<hash>/` and reused. The hash
  covers the tool sources, the pinned Roslyn versions, and the installed runtime — so an SDK bump
  or a plugin update rebuilds, and nothing else does.
- **Tier 1** keeps an on-disk **parse index** (`path + mtime + size` → declaration records). Only
  changed files are re-parsed: on Nomos (1086 files) a warm `search` is ~250 ms.
- **Tier 2** cannot use that index — `find-usages` needs live syntax trees and a Roslyn
  `Compilation`, which no on-disk cache can hold. Assembling them costs ~15 s on Nomos. If you're
  doing repeated semantic queries, run **`ds serve`** once: it holds the Solution in memory and a
  file watcher applies your edits incrementally.

```
find-usages on Nomos:   stateless 15,200 ms   ->   with `ds serve` 170 ms
```

**`ds serve` starts in the background and returns** once the daemon is warm — you do **not** need
`&`, `Start-Job`, or `nohup`, and you should not use them. It prints the pid and then exits:

```bash
ds serve --sln P:/…/Nomos.slnx
# daemon started (pid 15304) — warm in 3198 ms
#           21 projects, 1086 files, 0 edit(s) applied since start
```

`serve` is **opt-in**: Tier-2 commands use a daemon if one is running and fall back to stateless if
not, but they never start one for you (an auto-spawn would turn a burst of parallel agent commands
into a herd of processes each building a whole Solution). It idles out after 30 min; `ds serve --stop`
ends it. `--foreground` blocks instead, for debugging the daemon itself.

### Parallel agents

A daemon is identified by its **solution root** (plus the tool build). That makes the common cases
safe without any coordination:

| situation | what happens |
|---|---|
| agents on **different solutions** | independent daemons, different pipes — no interaction |
| several agents `serve` the **same** solution | exactly one daemon is started; the others wait for it and return the same pid |
| several agents **query** one daemon at once | requests queue and each is served in turn (~0.2–4 s), instead of falling back to the 15 s path |
| no daemon running | commands detect that from a presence marker and go stateless immediately — no connect timeout |

Requests are served **one at a time** on purpose: the command handlers capture output by swapping
the process-global `Console.Out`, so concurrent handling would interleave two agents' results.
Queueing is the correct trade.

Since the daemon is keyed by root, `ds serve` from any directory inside the tree targets the same
daemon (the solution is found by walking up). Pass `--sln` when you want to be sure.

## Accuracy & limits (what to trust)

- **Tier 1 signatures are rendered from syntax**, not resolved: a type reads exactly as written
  (`var` stays `var`, aliases aren't expanded). Right for navigation, not for exact type identity.
- **Tier 2 needs a restore, not a build.** References come from each project's
  `obj/project.assets.json` plus the shared frameworks — never from `bin/`, because a project's own
  `bin` dll would declare every type a second time and make symbols ambiguous. If a project was
  never restored, the tool restores it for you.
- **`.razor` components are invisible.** Razor's source generator runs in-memory and emits no `.cs`,
  so references to *Blazor component types* don't resolve. Everything else does. `discover --semantic`
  reports this explicitly (on Nomos: 18 unresolved names, all 4 Razor components; 20 of 21 projects
  fully clean).
- **The solution file is authoritative.** Projects on disk but absent from the `.slnx`/`.sln` are
  **not** scanned — `discover` says so, and `--root` scans them anyway. This matters: Nomos has 95
  `.csproj` on disk but only 21 in its `.slnx`; the other 73 are full copies under
  `.claude/worktrees/`. A naive glob would ingest the codebase 4.5× and quietly corrupt every count.
- **`unused` answers "nothing in this solution references it"**, which is not the same as "it is
  dead". It skips anything with an attribute or an `override`, because DI/EF/serialization/xunit
  invoke members reflectively where no reference exists. Non-public declarations only by default;
  **`--include-public`** adds public/protected ones — correct for a leaf app where the solution is
  the whole world, misleading for a library, where a public member with no local references *is* its
  API. Verify before deleting.
- **Explicit `<Compile Include/Remove>`** (i.e. `EnableDefaultCompileItems=false`) and linked files
  aren't honoured — files are assigned to their nearest ancestor `.csproj`.

## Output is greppable by design

One record per line, `file:line` clickable, columns aligned — so `ds search Foo | grep -i bar`
works, and the `// …` trailer lines carry counts and timings.

## Maintaining this skill — bugs & feature requests

Source & issues: **https://github.com/r-Larch/skills** (plugin `dotnet-source`).
Open a bug report or feature request at **https://github.com/r-Larch/skills/issues**.

This tool is meant to evolve. **If a command errors, produces wrong/partial output, is slow, or
doesn't support what you need**, do the right thing for where you're running it:

- **Running the installed plugin** (path contains `…/plugins/cache/…`): you **may edit this copy to
  unblock the task in progress** — but it is **regenerated on every update, so a cache-only edit is
  temporary and will be lost**. Therefore **every such edit MUST also produce an issue or a PR** so
  the change survives: open an issue at the URL above with the command, solution, and output (attach
  a diff if you edited), or — preferred, if you have push access — push the same change as a
  commit/PR. Never leave a cache-only fix undocumented; `/plugin marketplace update rlarch` will
  otherwise overwrite it. After editing `tool/`, the launcher rebuilds automatically (the cache key
  covers the sources).
- **Working in a checkout of the repo**: fix it, re-run the affected command to confirm, then commit
  & push. No version bump needed — every commit is picked up as an update.

Worth fixing/filing: a `--json` mode; honouring explicit `<Compile>` items; hosting the Razor source
generator so component types resolve; `go-to-def`; ranking `metrics` by a real complexity measure
rather than member counts. Prefer improving the shared layers (`Discovery.cs` = project/file set,
`Workspace.cs` = the Roslyn Solution, `Symbols.cs` = symbol resolution, `Index.cs` = the parse cache)
over duplicating logic in a command.

Layout: `SKILL.md` + `ds.ps1`/`ds.sh` (bootstrap launchers) + `tool/` (one compiled console app:
`Program` dispatch, `Discovery`, `Decls`+`Index` and `Tier1` for syntax, `Workspace`+`Symbols`+`Tier2`
for semantics, `Server` for the daemon). Nothing executes your code — Roslyn only ever parses and
binds it.

**Cross-platform**: Windows, macOS and Linux. The launchers are twins; the daemon uses named pipes
(implemented over unix domain sockets on macOS/Linux); cache paths honour `DOTNET_SOURCE_CACHE`,
else the OS temp dir.

To force a clean rebuild of the tool: delete `$TEMP/dotnet-source/tool/` (Windows:
`%TEMP%\dotnet-source\tool\`) and re-run. To drop a stale parse index, delete
`$TEMP/dotnet-source/index/`.
