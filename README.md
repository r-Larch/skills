# r-Larch/skills — Claude Code plugin marketplace (`rlarch`)

A personal [Claude Code](https://code.claude.com) marketplace with **one plugin, `rlarch`**, carrying
four skills: three for code navigation (.NET and TypeScript), one for running work too big for a
single context window.

## Install

```
/plugin marketplace add r-Larch/skills
/plugin install rlarch@rlarch
```

Or from the terminal:

```bash
claude plugin marketplace add r-Larch/skills
claude plugin install rlarch@rlarch
```

Restart Claude Code afterwards. Confirm with `claude plugin details rlarch` — it should list four
skills.

> **Already have `dotnet-reflect@rlarch` / `dotnet-source@rlarch` installed?** Those were separate
> plugins and are gone. Follow **[upgrade_guide.md](upgrade_guide.md)** — it takes two minutes, and
> there is one scope trap in it that will bite you if you improvise.

## The four skills

| Skill | Substrate | Use it for |
|---|---|---|
| [`dotnet-reflect`](#dotnet-reflect) | compiled DLLs | a **dependency you don't own** — signatures, docs, decompiled bodies, version diffs |
| [`dotnet-source`](#dotnet-source) | your `.cs` via Roslyn | the **.NET code you're editing** — search, outline (private included), find-usages, dead code |
| [`ts-source`](#ts-source) | your `.ts`/`.tsx` via the TS compiler API | the **frontend you're editing** — search, outline, JSX-aware find-usages, module graph, dead code |
| [`orchestrate`](#orchestrate) | your plan + subagents | an **undertaking that won't fit in one context** — delegate, verify, re-plan, commit |

The two .NET skills are complementary, not competing: reflect reads *metadata* (public surface only,
needs a built DLL), source reads *your source* (sees private, works unbuilt). `ts-source` is
`dotnet-source`'s twin on the other side of the stack — same commands, same tiering, same output
shape, so the two halves of a full-stack repo are navigated the same way.

### On names

Skills that ship in a plugin are namespaced: typed as slash commands they are `/rlarch:dotnet-reflect`,
`/rlarch:dotnet-source`, `/rlarch:ts-source`, `/rlarch:orchestrate`. **You rarely need to type them.** Automatic invocation
is unaffected by the prefix — describe the task and Claude loads the skill from its description. The
namespace cannot be turned off; see the upgrade guide if you want the trade-offs.

---

## `dotnet-reflect`

Inspects the API surface of any .NET **NuGet package or assembly** — read straight from the DLL on
disk, so it's exact for the version you actually have, not whatever version the web docs happen to
show.

It answers the questions you hit when using an unfamiliar package: *what's this type called, what's
the real signature, which overload, is this nullable, what changed between versions, who calls this,
what does this method actually do?*

### What it does

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

### Requirements

The **.NET 10 SDK** (`dotnet --version` ≥ 10) on the machine — the scripts are file-based C# apps.
Works on Windows, macOS, and Linux.

Claude invokes the skill automatically when you're working with an unfamiliar .NET package. You can
also drive the scripts directly:

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

### How it works

- **Load-only reflection** (`System.Reflection.MetadataLoadContext`) reads metadata without executing
  the package; the shared-framework directories are added to the resolver so web/framework base types
  resolve.
- **In-process decompilation** (`ICSharpCode.Decompiler`) — no global tools required.
- The **workbench** is a throwaway project built once per (package, version) under your temp dir and
  reused, so repeated queries are fast.

### Private / authenticated feeds

Packages on a private feed (e.g. GitHub Packages) work via your `nuget.config`. Point the scripts at
it with the `NUGET_API_CONFIG` env var (or run from inside a repo that has one); credentials
referenced as `%ENV_VAR%` expand from the environment at restore time. See the skill's `SKILL.md` for
details.

---

## `dotnet-source`

ReSharper-class navigation for the **solution you're editing**, from the terminal. It parses your
`.cs` with **Roslyn**, which buys the two things a metadata reader structurally cannot give you:
it **works on a solution that doesn't compile**, and it **sees `private`/`internal` members** — the
guts of the god-class you're actually untangling.

### What it does

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

```bash
# from $CLAUDE_PLUGIN_ROOT/skills/dotnet-source   (Windows: ./ds.ps1, unix: ./ds.sh)
./ds.ps1 metrics --sort methods --top 20      # find the god-classes
./ds.ps1 outline AknPersistenceService        # 30 members — 19 of them private
./ds.ps1 find-usages WhereTenantRead          # 62 call-sites + the declaration
./ds.ps1 impls ITenantContext
./ds.ps1 discover --semantic                  # project set + reference health
```

### Keeping the compilation alive

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

### How it works

- **Roslyn**, no MSBuild. The `Solution` is assembled in memory: the `.slnx`/`.sln` gives the
  authoritative project set, references come from each project's `obj/project.assets.json` plus the
  shared frameworks, and `<ProjectReference>` edges are wired transitively.
- **Never from `bin/`** — a project's own output dll would declare every one of its types a second
  time and make symbols ambiguous. `assets.json` needs only a restore, never a compile.
- Nothing executes your code; Roslyn only parses and binds it.

Validated against `dotnet-reflect` on a real solution: both tools independently find the **same 62
call-sites** for a symbol, and `dotnet-source` additionally reports the declaration that IL can't see.

The design notes behind the tool live in [`docs/dotnet-source-design.md`](docs/dotnet-source-design.md).

---

## `ts-source`

`dotnet-source`'s twin for the **TypeScript/JavaScript project you're editing**. It parses your
`.ts`/`.tsx`/`.js`/`.jsx` with the **TypeScript compiler API**, so it works on a project that
doesn't compile, sees declarations that aren't exported, and — unlike grep — knows that `<Badge/>`
in JSX is a reference to `Badge` while `BadgeProps` is not.

Built for Vite + SolidJS/React apps and pnpm/npm workspaces.

### What it does

| Command | Answers |
|---|---|
| `search` | "what's it called" — by name **and kind** (`component`, `hook`, `primitive`, `type`, …) |
| `outline` | a declaration's members, or a whole file in line order — **non-exported included** |
| `tree` | directory → files / declarations / exports / lines |
| `metrics` | rank files or types by size — **god-file detection** |
| `graph` | the import graph for one module: who pulls it in, and which names they take |
| `dead` | **three levels of dead code** — see below |
| `find-usages` | "who uses this" — JSX usage, imports, call sites, the declaration |
| `impls` | who implements this interface / extends this class |
| `calls` | call hierarchy (`--callers` / `--callees`) |
| `serve` | keep the LanguageService warm (see below) |
| `discover` | what the tool actually sees — start here if a count looks wrong |

**Tier 1** (`search`/`outline`/`tree`/`metrics`/`graph`/`dead`) needs **no build and no typecheck**.
**Tier 2** (`find-usages`/`impls`/`calls`) needs `node_modules` installed — still **not** a build.

```bash
# from $CLAUDE_PLUGIN_ROOT/skills/ts-source   (Windows: ./tss.ps1, unix: ./tss.sh)
./tss.ps1 metrics --sort loc --top 20         # find the files that got out of hand
./tss.ps1 outline --file src/utils/http.ts    # 42 declarations, most of them not exported
./tss.ps1 find-usages Badge                   # 106 JSX usages + 25 imports + the declaration
./tss.ps1 graph src/utils/http.ts --importers # blast radius of a change
./tss.ps1 dead                                # what can I delete?
```

### Dead code, in three levels

The reason this skill exists. An ES-module codebase can answer a strictly stronger question than a
.NET solution can: not "does any reference exist" but **"is this reachable from an entry point"** —
from syntax alone, no program build. So `dead` is seconds, not minutes.

| Level | Question | Confidence |
|---|---|---|
| `--files` | no entry point reaches this module | highest — also catches **transitively dead** clusters |
| `--exports` | no other module imports this name | high — split into **deletable** vs **over-exported** |
| `--locals` | declared in a file, never read there | exact, but needs a Program (slower) |

That `--exports` split is the part most tools get wrong. "Nobody imports this" covers both a function
you can delete and a helper that is merely exported for no reason, and they need opposite actions.
On a real 644-file SolidJS app: **75 deletable, 405 just over-exported** — drop the `export` keyword,
don't delete the code.

The safety rails matter as much as the findings, because people act on this output by deleting
files. With **no entry points found** the command **refuses to run** rather than declare the whole
project dead; if **>35% of files** come back unreachable it warns that you are almost certainly
missing an entry point; `import * as ns` and bare dynamic imports make a module's exports all-live;
Solid `use:` directives are never reported; and barrels are **charged through**, so
`export * from './PagedList'` does not make `PagedList` look used.

### Keeping the compilation alive

- **Tier 1** — an on-disk parse index keyed by `path + mtime + size`; only changed files re-parse.
  One parse yields both the declarations and the import edges, so nothing pays twice.
- **Tier 2** — a `LanguageService` can't be serialised, so there's an opt-in daemon:

```
on a 644-file SolidJS app:   find-usages   stateless 4,612 ms  →  `tss serve` 391 ms
                             impls         stateless 4,958 ms  →  `tss serve` 153 ms
```

`serve` is opt-in and returns once warm — same contract as `ds serve`, same herd-avoidance.

### How it works

- **No build step and no install.** The tool is plain ES modules with `// @ts-check` JSDoc types, run
  directly by Node — nothing to compile, nothing to cache-invalidate, ~150 ms cold start.
- **It analyses with the project's own TypeScript**, resolved from `node_modules`. This is the
  opposite of `dotnet-source`'s pinned-Roslyn rule and lands there for the same reason: an older
  parser turns newer syntax into error nodes and silently drops declarations, and in the npm world
  it's the project — not the tool — that decides how new the syntax is. Using its compiler makes
  that self-pinning. A project with no TypeScript gets a pinned copy installed into the cache once.
- **The `tsconfig.json` is authoritative for the file set**, the way the `.slnx` is for
  `dotnet-source` — a naive `**/*.ts` glob would ingest `node_modules`, `dist` and every worktree
  copy and quietly corrupt every count.
- Nothing executes your code; the compiler API only ever parses and binds it.

---

## `orchestrate`

For work that will not fit in one context window: executing a written plan, implementing a spec
end-to-end, a multi-phase refactor or migration. Invoking it promotes the main session to an
**orchestrator** — it stops writing code and starts delegating, verifying, and deciding.

Trigger it by saying what you want done ("execute this plan", "implement this RFC end to end", "do
this migration"), or type `/rlarch:orchestrate`.

### What it does

The main context is the only thing in the system that holds the *whole* picture, and it is the thing
that runs out first. The skill spends it deliberately:

| Rule | Why |
|---|---|
| **The orchestrator never writes product code** | work reaches the repo only through worker subagents, so implementation detail never enters the context that must survive |
| **Writers run strictly one at a time** | two agents editing one repo produce conflicts you pay for in the one resource you can't afford; parallelism is allowed only for read-only recon |
| **Every verification command is run by the orchestrator, not trusted from a report** | "tests pass" is the claim you'd most regret taking on faith, and checking costs seconds |
| **Disk is the memory, not context** | every decision, assumption and result lands in a run ledger the moment it happens, so a fresh session can resume from `LEDGER.md` alone |
| **Red-checks** | any test defending a fix must be confirmed *failing* against the un-fixed code — the cheapest proof in the system, run in a context that's being thrown away anyway |
| **Reviews are rare and priced** | a review agent costs more than an extra worker task; there's an explicit risk dial for when to spend one, and a phase gate that looks only at the seams between tasks |
| **The plan is audited, not just executed** | every couple of phases a fresh agent checks the phases you *haven't* run yet — above all whether each task's verification command could actually go red, because a filter that already passes gets an unimplemented task accepted by every control in the system |

It also carries a hard ceiling on deliberation — an evidence budget per open question, a
reversibility test, and explicit escalation triggers — because rigor that never terminates isn't
rigor.

### The run directory

Phase 0 creates `.claude/orchestration/<slug>/`:

| File | Contents |
|---|---|
| `PLAN.md` | the goal paragraph, phases, tasks, done-criteria |
| `CONVENTIONS.md` | the repo's binding facts and safety rules, written **once**; every worker brief points at it instead of restating it |
| `LEDGER.md` | the running record — one line per task |

You approve the plan once, before the first writing agent runs. After that it runs to completion
unless an escalation trigger fires.

---

## The self-improvement loop

`orchestrate` critiques itself, and the critique is a normal repo workflow rather than a vibe.

1. **Retro.** At close-out, *only if the run earned it* — a rework, a re-plan, a red verification, a
   gate blocker, or one traceable ≥10k-token waste caused by the skill's own guidance — the skill
   files a GitHub issue against this repo, labelled `orchestrate-retro`, proposing **one** concrete
   edit to **one** of its own files. It's built from the ledger it already holds, plus at most one
   read — the file it proposes to edit, so the before/after is verbatim. No recon, no review agent.
   A clean run teaches nothing and is skipped. It posts unasked **only where you have push access
   to this repo**; anyone else gets the same retro written to `.claude/orchestration/<slug>/RETRO.md`
   and nothing is posted. Rules and the exact issue schema live in
   `plugins/rlarch/skills/orchestrate/references/retro.md`.
2. **Triage and apply.** The repo-local `/apply-retro` command
   (`.claude/commands/apply-retro.md`) reads the open issues, ranks them by *recurrence* — the only
   signal here that isn't a model's opinion of its own instructions — enforces an aggregate token
   budget on the batch, applies what survives, and opens a PR.
3. **Close the loop.** Applied issues close with a link to the PR; rejected ones close as *not
   planned* with a stated reason. The retro's duplicate search covers closed issues, so a rejected
   proposal is read as **do not re-file** and never comes back — while a *closed-as-applied* match
   that recurs does, because that edit demonstrably didn't hold.

The label is created once per repo:

```bash
gh label create orchestrate-retro \
  --repo r-Larch/skills \
  --description "Self-proposed edit to the orchestrate skill, filed by its own retro" \
  --color 5319E7
```

`/apply-retro` is a maintainer command in this repo's `.claude/commands/`. It is not part of the
plugin and is not installed with it.

---

## Layout

```
.claude-plugin/marketplace.json          # marketplace "rlarch" — one plugin entry
.claude/commands/apply-retro.md          # maintainer command, not shipped with the plugin
.gitignore
docs/dotnet-source-design.md             # internal design notes for the dotnet-source tool
plugins/rlarch/                          # the plugin
  .claude-plugin/plugin.json
  skills/dotnet-reflect/
    SKILL.md                             # instructions Claude follows
    scripts/{common,reflect}.cs          # shared helpers (#:include'd)
    scripts/{find,find-usages,surface,decompile,diff,cache,bindir}.cs
  skills/dotnet-source/
    SKILL.md
    ds.ps1 / ds.sh                       # bootstrap launchers (build once, cache, exec)
    tool/                                # one compiled console app (net10.0 + Roslyn)
      DotnetSource.csproj
      Args.cs Common.cs Decls.cs Discovery.cs Index.cs Program.cs
      Server.cs Symbols.cs Tier1.cs Tier2.cs Workspace.cs
  skills/ts-source/
    SKILL.md
    tss.ps1 / tss.sh                     # launchers (~10 lines: no build, no install)
    tool/                                # plain ES modules (@ts-check + JSDoc), Node 18+
      tsconfig.json                      # dev-only: `tsc --noEmit` checks the JSDoc types
      cli.mjs main.mjs args.mjs common.mjs tsapi.mjs
      discovery.mjs decls.mjs cache.mjs tier1.mjs
      graph.mjs dead.mjs program.mjs tier2.mjs discover.mjs server.mjs
  skills/orchestrate/
    SKILL.md
    references/{decompose,briefs,retro}.md
    assets/{CONVENTIONS,LEDGER}.template.md
README.md
upgrade_guide.md
```

Adding another skill later: drop it in `plugins/rlarch/skills/<name>/`. A plugin auto-discovers its
`skills/` directory — nothing to declare in `plugin.json`. **You must still bump `version` in
`plugin.json`**: the updater gates on that field and caches the payload per version, so a pushed
commit without a bump reaches nobody. See **[AGENTS.md](AGENTS.md)** for the release checklist.

## Bugs & feature requests

Open an issue: **https://github.com/r-Larch/skills/issues**

## License

MIT (adjust to taste).
