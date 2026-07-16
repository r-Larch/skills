# r-Larch/skills — Claude Code plugin marketplace (`rlarch`)

A personal [Claude Code](https://code.claude.com) plugin marketplace. Add it with
`/plugin marketplace add r-Larch/skills`, then install any plugin below.

## Plugins

| Plugin | Substrate | Use it for |
|---|---|---|
| [`dotnet-reflect`](#dotnet-reflect) | compiled DLLs | a **dependency you don't own** — signatures, docs, decompiled bodies, version diffs |
| [`dotnet-source`](#dotnet-source) | your `.cs` via Roslyn | the **code you're editing** — search, outline (private included), find-usages, dead code |

They're complementary, not competing: reflect reads *metadata* (public surface only, needs a built
DLL), source reads *your source* (sees private, works unbuilt).

---

### `dotnet-reflect`

Inspects the API surface of any .NET **NuGet package or assembly** — read straight from the DLL on
disk, so it's exact for the version you actually have, not whatever version the web docs happen to
show.

It answers the questions you hit when using an unfamiliar package: *what's this type called, what's
the real signature, which overload, is this nullable, what changed between versions, who calls this,
what does this method actually do?*

## What it does

Seven one-command scripts. Each takes a **package id + version** (or a `--bin` folder) and builds
everything it needs itself (no DLL/dependency/XML hunting):

| Command | Answers |
|---|---|
| `find` | "what is it called / where does it live" — locate types & members by name |
| `find-usages` | "who calls this / where is it used" — reverse usages, read from compiled IL, with file:line |
| `surface` | exact signatures + XML-doc summaries (the default digest) |
| `decompile` | real C# **with method bodies** — the behavior signatures can't show |
| `diff` | API changelog between two versions (added/removed types & members) |
| `cache` | where a package lives in the local NuGet cache, its versions & files |
| `bindir` | build a reusable workbench bin folder and print its path |

Signatures are **metadata-accurate**: nullable value **and** reference types (`HttpStatusCode?`,
`string?`, `Func<HttpContext,String?>?`), `required` members, `[SetsRequiredMembers]`, `[Obsolete]`,
`static`/`virtual`/`override`, and a C#-style header with directly-declared interfaces
(`class X : Base, IFoo`). Metapackages (e.g. `OpenIddict.AspNetCore`) expand to the real assemblies
they expose.

## Install

```
/plugin marketplace add r-Larch/skills
/plugin install dotnet-reflect@rlarch
```

Requires the **.NET 10 SDK** (`dotnet --version` ≥ 10) on the machine — the scripts are file-based
C# apps. Works on Windows, macOS, and Linux.

Once installed, Claude invokes it automatically when you're working with an unfamiliar .NET package.
You can also drive the scripts directly:

```bash
# from the plugin's scripts directory ($CLAUDE_PLUGIN_ROOT/skills/dotnet-reflect/scripts)
dotnet run find.cs        OpenAI 2.12.0 Streaming
dotnet run surface.cs     OpenAI 2.12.0 Chat.ChatClient
dotnet run decompile.cs   OpenAI 2.12.0 OpenAI.Chat.ChatClient
dotnet run diff.cs        OpenAI 2.11.0 2.12.0
dotnet run find-usages.cs --bin MyApp/bin/Debug/net10.0 --only MyApp IsDevelopment
```

`version` may be the literal `latest`. Use `surface.cs --inherited` to also list base-type members.
`find-usages.cs` reads compiled IL — point `--bin` at your built solution output; a portable PDB adds
`file:line` and the source line.

## How it works

- **Load-only reflection** (`System.Reflection.MetadataLoadContext`) reads metadata without executing
  the package; the shared-framework directories are added to the resolver so web/framework base types
  resolve.
- **In-process decompilation** (`ICSharpCode.Decompiler`) — no global tools required.
- The **workbench** is a throwaway project built once per (package, version) under your temp dir and
  reused, so repeated queries are fast.

## Private / authenticated feeds

Packages on a private feed (e.g. GitHub Packages) work via your `nuget.config`. Point the scripts at
it with the `NUGET_API_CONFIG` env var (or run from inside a repo that has one); credentials
referenced as `%ENV_VAR%` expand from the environment at restore time. See the skill's `SKILL.md` for
details.

---

### `dotnet-source`

ReSharper-class navigation for the **solution you're editing**, from the terminal. It parses your
`.cs` with **Roslyn**, which buys the two things a metadata reader structurally cannot give you:
it **works on a solution that doesn't compile**, and it **sees `private`/`internal` members** — the
guts of the god-class you're actually untangling.

#### What it does

| Command | Answers |
|---|---|
| `search` | "what's it called" — by name **and kind**, with signatures |
| `outline` | a type's full member list — **private included**, partial parts merged |
| `tree` | project → namespace → type map |
| `metrics` | rank types by size — **god-class detection** |
| `find-usages` | "who uses this" — call-sites **and** declarations/locals/overrides |
| `impls` | who implements this interface / derives from this base |
| `calls` | call hierarchy (`--callers` / `--callees`) |
| `unused` | declared but never referenced |
| `serve` | keep the Roslyn compilation warm (see below) |
| `discover` | what the tool actually sees — start here if a count looks wrong |

**Tier 1** (`search`/`outline`/`tree`/`metrics`) needs **no build at all**.
**Tier 2** (the semantic four) needs a `dotnet restore` — still **not** a build.

#### Install

```
/plugin marketplace add r-Larch/skills
/plugin install dotnet-source@rlarch
```

```bash
# from $CLAUDE_PLUGIN_ROOT/skills/dotnet-source   (Windows: ./ds.ps1, unix: ./ds.sh)
./ds.ps1 metrics --sort methods --top 20      # find the god-classes
./ds.ps1 outline AknPersistenceService        # 30 members — 19 of them private
./ds.ps1 find-usages WhereTenantRead          # 62 call-sites + the declaration
./ds.ps1 impls ITenantContext
./ds.ps1 discover --semantic                  # project set + reference health
```

#### Keeping the compilation alive

The tool is **one compiled binary**, not a file-based script: it's built once into a hash-keyed
cache (keyed on sources + pinned Roslyn versions + runtime band) and reused at **~100 ms startup**.
On top of that, two layers keep work alive between calls:

- **Tier 1** — an on-disk parse index keyed by `path + mtime + size`; only changed files re-parse.
- **Tier 2** — can't use an index (it needs live syntax trees and a `Compilation`), so there's an
  opt-in daemon:

```
find-usages on a 21-project / 1086-file solution:   stateless 15,200 ms  →  `ds serve` 170 ms
```

`serve` is opt-in — commands use it if it's running and fall back to stateless if not; they never
spawn one for you. A file watcher applies your edits incrementally.

#### How it works

- **Roslyn**, no MSBuild. The `Solution` is assembled in memory: the `.slnx`/`.sln` gives the
  authoritative project set, references come from each project's `obj/project.assets.json` plus the
  shared frameworks, and `<ProjectReference>` edges are wired transitively.
- **Never from `bin/`** — a project's own output dll would declare every one of its types a second
  time and make symbols ambiguous. `assets.json` needs only a restore, never a compile.
- Nothing executes your code; Roslyn only parses and binds it.

Validated against `dotnet-reflect` on a real solution: both tools independently find the **same 62
call-sites** for a symbol, and `dotnet-source` additionally reports the declaration that IL can't see.

## Layout

```
.claude-plugin/marketplace.json          # marketplace "rlarch" (repo root)
dotnet-reflect/                          # plugin: compiled-assembly inspection
  .claude-plugin/plugin.json
  skills/dotnet-reflect/
    SKILL.md                             # instructions Claude follows
    scripts/{common,reflect}.cs          # shared helpers (#:include'd)
    scripts/{find,find-usages,surface,decompile,diff,cache,bindir}.cs
dotnet-source/                           # plugin: Roslyn source navigation
  .claude-plugin/plugin.json
  skills/dotnet-source/
    SKILL.md
    ds.ps1 / ds.sh                       # bootstrap launchers (build once, cache, exec)
    tool/                                # one compiled console app (net10.0 + Roslyn)
      Program.cs Discovery.cs Decls.cs Index.cs Tier1.cs
      Workspace.cs Symbols.cs Tier2.cs Server.cs
```

Adding another plugin later: drop it in its own top-level folder and add an entry to
`.claude-plugin/marketplace.json`.

## Bugs & feature requests

Open an issue: **https://github.com/r-Larch/skills/issues**

## License

MIT (adjust to taste).
