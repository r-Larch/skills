---
name: ts-source
description: >-
  Navigate the TypeScript/JavaScript SOURCE project you are editing, ReSharper-style,
  from the terminal — search for a declaration by name and kind (component, hook,
  type, class…), outline a file or type's full member list INCLUDING non-exported
  members, map directory → files → exports, rank files by size, walk the module
  import graph, and answer "who uses this" semantically (find-usages that resolves
  JSX `<Card/>` usage, not just text), "who implements this", "who calls this", and
  "what is never used". Finds DEAD CODE three ways: unreachable FILES (nothing an
  entry point imports), unused EXPORTS (split into deletable vs merely
  over-exported), and unused locals/imports. Reads .ts/.tsx/.js/.jsx with the
  TypeScript compiler API, so Tier 1 works on a project that DOESN'T COMPILE and
  sees the non-exported guts a bundler report cannot. Built for Vite + SolidJS/React
  apps and pnpm/npm workspaces. USE FOR: "where is X used / who calls X" in my own
  code, "what's in this file", "what components does this export", "is this dead
  code", "what can I delete", "which utils are unused", "map this frontend", "find
  the biggest files", navigating an unfamiliar or mid-refactor TS codebase.
  DO NOT USE FOR: an npm package you don't own (read its .d.ts in node_modules), a
  .NET solution (use dotnet-source), or a birds-eye architectural map with
  communities and relationships (use graphify).
---

# ts-source — source-level navigation for the TS/JS project you're editing

One Node process, git-style subcommands. It parses your `.ts`/`.tsx`/`.js`/`.jsx` with the
**TypeScript compiler API**, so:

- **No build, no bundle, no typecheck** for Tier 1 — it works mid-refactor, on code that doesn't compile.
- **It sees what isn't exported** — the guts of the 700-line component you're untangling.
- **It resolves JSX** — `<Badge/>` is a reference to `Badge`; grep can't tell that from `BadgeProps`.
- **It knows the module graph** — which is how it answers "what is dead" exactly, and fast.

**Locating the tool (do this first).** Resolve the skill directory, then call the launcher for your
OS. Everything below is written as `tss <command>`; substitute accordingly.

- Installed as a **plugin**: `"$CLAUDE_PLUGIN_ROOT/skills/ts-source"` (run `echo "$CLAUDE_PLUGIN_ROOT"` to confirm).
- A **local skill** or repo checkout: the folder containing this SKILL.md.

```bash
# Windows (PowerShell)          # macOS / Linux
./tss.ps1 <command> …           ./tss.sh <command> …
```

Needs **Node 18+**. There is **no build step and no install** — unlike `dotnet-source`, the tool is
plain ES modules and the compiler it analyses with is **the one your project already resolves**
(`node_modules/typescript`). Cold start is ~150 ms. If a project has no TypeScript at all (a plain
JS repo), it installs a pinned copy into its own cache once, so Tier 1 still works.

## The commands

```bash
# ---- Tier 1: syntax only. No build. Sees non-exported members. Works on broken code. ------

# SEARCH — find a declaration by name. Beyond grep: filter by kind, get the signature.
tss search <pattern> [--kind component,hook,primitive,fn,class,interface,type,enum,const,method,prop]
                     [--regex] [--exported] [--top N] [--json]
#   e.g. tss search Badge --kind component   -> every *Badge* component + file:line + props

# OUTLINE — a declaration and its members, or an entire file in line order.
tss outline <Name|pattern>
tss outline --file <path.tsx>               # everything declared in one file, NON-EXPORTED INCLUDED
#   e.g. tss outline --file src/utils/http.ts  -> 42 declarations, most of them not exported

# TREE — directory -> files / declarations / exports / lines.
tss tree [dirFilter]

# METRICS — rank by size. God-file / god-component detection.
tss metrics [--sort loc|decls|exports|imports|components] [--of file|type] [--top N]
#   --of type --sort members ranks DTOs and prop-bags to the top; --of file --sort loc is
#   usually what you want when hunting the file that has grown out of control.

# GRAPH — the import graph for one module. No .NET analogue.
tss graph <file> [--importers|--imports]
#   "who pulls this in, and which names do they take" — the fastest way to judge a refactor's blast
#   radius, and how you check a `dead` finding before deleting.

# ---- Dead code: the module graph. Exact, and still Tier 1 (seconds, not minutes). --------

tss dead [--files] [--exports] [--locals] [--entry <glob,…>] [--include-public] [--include-tests] [--top N]
#   default: --files --exports.  See "Finding dead code" below — read it before deleting anything.

# ---- Tier 2: semantic. Needs node_modules installed — NOT a build. -----------------------

# FIND-USAGES — every reference: JSX usage, imports, call sites, the declaration.
tss find-usages <symbol>
#   <symbol> = Name | Owner.Name | path-fragment.Name  (matched at dotted boundaries)
#   e.g. tss find-usages Badge   -> 106 usages + 25 imports + 1 declaration across 26 files

# IMPLS — who implements this interface / extends this class.
tss impls <TypeOrInterface> [--derived]

# CALLS — call hierarchy.
tss calls <fn> [--callers|--callees]

# ---- Keep-alive (optional, Tier 2 only) -------------------------------------------------

# SERVE — starts in the background and RETURNS once warm. Do not background it yourself.
tss serve --project <tsconfig.json>   # find-usages: ~4.6s -> ~0.4s for every later query
tss serve --stop                      # stop it     tss serve --foreground   # block (debugging)
tss status                            # is a daemon serving this project? (pid, log)

# ---- Utility ----------------------------------------------------------------------------

tss discover [--semantic]  # what the tool actually sees. START HERE if any count looks wrong.
tss version
```

**Target selection — every command, `serve` and `status` included:**

| flag | meaning |
|---|---|
| `--project <tsconfig.json>` | the project to work on. **Prefer this.** It's authoritative, and for `serve` it's what *identifies* the daemon. |
| `--root <dir>` | no tsconfig: scan this directory tree |
| `--ts <path>` | analyse with a specific TypeScript compiler instead of the project's own |

Default: walk up from cwd for `tsconfig.json`, else treat cwd as the root — so running from
anywhere inside the tree finds the same project, and therefore the same daemon. Flags may precede
the verb (`tss --root . dead`) — the verb is hoisted.

## How to use it (escalation ladder)

1. **Don't know what it's called?** → `search <substring>` (add `--kind component` to cut noise).
2. **Want the shape of a file?** → `outline --file <path>` — non-exported declarations included.
3. **Where is this used / who renders this component?** → `find-usages <Name>`.
4. **What breaks if I change this module?** → `graph <file> --importers`.
5. **Untangling a monster file?** → `metrics --sort loc` to find it, `outline --file` to see it,
   `find-usages` on each export to see what actually depends on it.
6. **What can I delete?** → `dead` — then read the next section, twice.
7. **Counts look wrong?** → `discover` (and `discover --semantic` for reference health).

## Finding dead code

This is the command people reach for, and the one most able to do damage, so it is built in three
levels that answer three different questions. **Run them in this order** — the confidence drops as
you go down.

```bash
tss dead --files      # 1. whole modules no entry point reaches
tss dead --exports    # 2. exported names nobody imports
tss dead --locals     # 3. locals/imports never read in their own file (slower: builds a Program)
```

**1. `--files` — the strongest signal.** Reachability from entry points across the import graph.
Not "nobody imports it" but "nothing you actually ship reaches it", which additionally catches
**transitively dead** clusters: a 350-line file whose only importer is itself dead. Entry points are
seeded from `index.html` `<script>` tags, `package.json` `main`/`module`/`exports`/`bin`,
`*.config.*`, `scripts/`, service workers and tests — add your own with `--entry`.

**2. `--exports` — split into two buckets, because they need opposite actions:**

- **`[1] DELETABLE`** — not imported anywhere *and* not used inside its own file. Actually removable.
- **`[2] OVER-EXPORTED`** — used only inside its own file. **Drop the `export` keyword, don't delete.**

Most tools report these as one list, which is why their output gets ignored. On a real 644-file
SolidJS app the split was 75 deletable vs 405 over-exported — a completely different afternoon.

**3. `--locals`** — the compiler's own `noUnusedLocals`/`noUnusedParameters` diagnostics. Exact, but
needs a Program (~15 s cold, ~1 s under `serve`).

**The safety rails**, because a wrong answer here gets code deleted:

- With **no entry points found**, `dead` **refuses to run** instead of declaring the whole project
  dead. It tells you to pass `--entry`.
- If **more than 35% of files** come back unreachable, it prints a warning: that is nearly always a
  missing entry point, not a codebase that is one-third dead.
- A module reached by `import * as ns` or a bare `import('./x')` has **all** its exports treated as
  live — the tool cannot see which names are touched, and under-reporting is the correct failure.
- Names bound by a **`use:` directive** (Solid) are never reported.
- **Barrels are charged through.** `export * from './PagedList'` does *not* make `PagedList` look
  used; only someone actually importing that name from the barrel does.

**Always verify before deleting** — `graph <file> --importers` and `find-usages <name>` take a
second each and turn a plausible finding into a certain one.

## Which skill? (routing)

| You want | Use |
|---|---|
| the **TS/JS you're editing** — non-exported members, exact usages, dead code, works unbuilt | **ts-source** (this) |
| the **.NET solution you're editing** | **dotnet-source** — the same shape, over Roslyn |
| a **NuGet package** symbol you don't own | **dotnet-reflect** — reads the compiled DLL |
| a **birds-eye architectural map**, communities, relationships | **graphify** |

`find-usages createSignal` returns nothing here **by design**: `createSignal` is declared in
`solid-js`, not in your source. This tool finds what's declared in *your* project. For a symbol from
a package, read its `.d.ts` under `node_modules/<pkg>` directly.

## Keeping the compilation alive

Two layers, because the tiers have different costs:

- **Tier 1** keeps an on-disk **parse index** (`path + mtime + size` → declaration records + module
  edges). Only changed files are re-parsed: on a 644-file app a warm `search` is ~280 ms and
  `dead --files` is ~380 ms. One parse yields both the declarations and the import edges, so no
  command pays for a second pass.
- **Tier 2** cannot use that index — `find-usages` needs a live `LanguageService`, which no on-disk
  cache can hold. Assembling one costs ~4 s. If you're doing repeated semantic queries, run
  **`tss serve`** once: it holds the service in memory and re-versions files you edit.

```
on a 644-file SolidJS app:   find-usages   stateless 4,612 ms  ->  with serve 391 ms
                             impls         stateless 4,958 ms  ->  with serve 153 ms
```

**`tss serve` starts in the background and returns** once the daemon is warm — you do **not** need
`&`, `Start-Job`, or `nohup`, and you should not use them. It prints the pid and then exits:

```bash
tss serve --project P:/…/nomos/tsconfig.json
# daemon running (pid 211164) — 644 files, project …/nomos/tsconfig.json
```

`serve` is **opt-in**: Tier-2 commands use a daemon if one is running and fall back to stateless if
not, but they never start one for you (an auto-spawn would turn a burst of parallel agent commands
into a herd of processes each building a whole program). It idles out after 30 min;
`tss serve --stop` ends it.

### Parallel agents

A daemon is identified by its **resolved tsconfig path**. That makes the common cases safe without
any coordination:

| situation | what happens |
|---|---|
| agents on **different projects** | independent daemons, different pipes — no interaction |
| several agents `serve` the **same** project | exactly one daemon is started; the others get the same pid |
| several agents **query** one daemon at once | requests queue and each is served in turn, instead of falling back to the 4 s path |
| no daemon running | commands detect that from a presence marker and go stateless immediately — no connect timeout |

Requests are served **one at a time** on purpose: command handlers capture output by swapping a
module-level sink, so concurrent handling would interleave two agents' results.

## Accuracy & limits (what to trust)

- **Tier 1 signatures are rendered from syntax**, not resolved: a type reads exactly as written
  (`Accessor<T>` stays `Accessor<T>`, an inferred `const` has no stated type). Right for navigation,
  not for exact type identity.
- **Kinds are convention-based.** `component` = PascalCase + returns JSX; `hook` = `use*`;
  `primitive` = `create*`. In a Solid/React codebase the naming convention *is* the contract, but a
  component named `renderThing` will be classified `fn`.
- **Tier 2 needs `node_modules`, not a build.** If a project was never installed, module resolution
  fails and results near those imports are incomplete — `discover --semantic` reports this as a
  count of unresolvable modules (TS 2307).
- **The tsconfig is authoritative for the file set.** Files on disk but excluded from it are **not**
  scanned; `discover` says so, and `--root` scans them anyway. This matters — a naive `**/*.ts` glob
  ingests `node_modules`, `dist` and every worktree copy and quietly corrupts every count.
- **`dead` answers "nothing in this project reaches/imports it"**, which is not the same as "it is
  dead". A route named by string, a component a plugin mounts, a file only the bundler references —
  all are used without a resolvable reference. Read the safety rails above and verify.
- **CSS/asset imports are bundler edges, not module edges.** `import './x.module.scss'` is tracked
  as an asset and never reported as a broken import, but a `.scss` file is not itself analysed, so
  an orphaned stylesheet won't be found.
- **`--include-public`** matters in a workspace: a package's `exports` entry file *is* its API, so
  its unused exports are excluded by default. Pass the flag in a leaf app where the project is the
  whole world.
- **Declaration merging over-counts local uses.** A `type X` and `const X` sharing a name inflate
  each other's local-reference count, which can move a finding from `DELETABLE` to `OVER-EXPORTED` —
  deliberately the safe direction.

## Output is greppable by design

One record per line, `file:line` clickable, columns aligned — so `tss search Foo | grep -i bar`
works, and the `// …` trailer lines carry counts and timings. `search` and `find-usages` also take
`--json`.

## Maintaining this skill — bugs & feature requests

Source & issues: **https://github.com/r-Larch/skills** (plugin `ts-source`).
Open a bug report or feature request at **https://github.com/r-Larch/skills/issues**.

This tool is meant to evolve. **If a command errors, produces wrong/partial output, is slow, or
doesn't support what you need**, do the right thing for where you're running it:

- **Running the installed plugin** (path contains `…/plugins/cache/…`): you **may edit this copy to
  unblock the task in progress** — but it is **regenerated on every update, so a cache-only edit is
  temporary and will be lost**. Therefore **every such edit MUST also produce an issue or a PR** so
  the change survives: open an issue at the URL above with the command, project, and output (attach
  a diff if you edited), or — preferred, if you have push access — push the same change as a
  commit/PR. Never leave a cache-only fix undocumented; `/plugin marketplace update rlarch` will
  otherwise overwrite it.
- **Working in a checkout of the repo**: fix it, re-run the affected command to confirm, then commit
  & push. Then **bump `version` in `plugins/rlarch/.claude-plugin/plugin.json`** — the
  installed copy is cached per version (`…/plugins/cache/rlarch/rlarch/<version>/`) and the updater
  gates on that field, not on the commit sha. Without a bump, `/plugin` answers "already at the
  latest version" and your commit never reaches anyone.

The sources are plain `.mjs` with `// @ts-check` and JSDoc types — **no build step**. Type-check
them with `npx tsc --noEmit -p tool/tsconfig.json` (that tsconfig is dev-only; nothing reads it at
runtime).

Worth fixing/filing: reporting orphaned CSS/asset files; honouring `vite.config.ts` `resolve.alias`
(only tsconfig `paths` are honoured today); a `--fix` that strips `export` from the OVER-EXPORTED
bucket; unused *props* on a component; framework-aware entry points (Nuxt/Next/SvelteKit routing
conventions). Prefer improving the shared layers (`discovery.mjs` = project/file set/entry points,
`graph.mjs` = the module graph, `decls.mjs` = the one-pass parser, `cache.mjs` = the parse index,
`program.mjs` = the LanguageService) over duplicating logic in a command.

Layout: `SKILL.md` + `tss.ps1`/`tss.sh` (launchers, ~10 lines each) + `tool/` (plain ES modules:
`cli` entry, `main` dispatch, `discovery`, `decls`+`cache` and `tier1` for syntax, `graph`+`dead`
for the module graph, `program`+`tier2` for semantics, `server` for the daemon). Nothing executes
your code — the compiler API only ever parses and binds it.

**Cross-platform**: Windows, macOS and Linux. The launchers are twins; the daemon uses named pipes
on Windows and unix domain sockets elsewhere (Node's `net` handles both); cache paths honour
`TS_SOURCE_CACHE`, else the OS temp dir.

To drop a stale parse index, delete `$TMPDIR/ts-source/index/` (Windows: `%TEMP%\ts-source\index\`).
