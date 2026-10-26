# r-Larch/skills — Claude Code plugin marketplace (`rlarch`)

A personal [Claude Code](https://code.claude.com) marketplace with **one plugin, `rlarch`**, carrying
eight skills: three for code navigation (.NET and TypeScript), three for running work too big for a
single context window, one for cleaning up comments, and one that sets up
[WorkState](#workstate) — the server `orchestrate` and `controller` require and record their work in.

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

Restart Claude Code afterwards. Confirm with `claude plugin details rlarch` — it should list eight
skills.

> **Upgrading from 1.x?** `orchestrate` and `controller` now require WorkState. Read
> **[Upgrading from 1.x to 2.x](upgrade_guide.md#upgrading-from-1x-to-2x)**; what changed in each
> version is in **[CHANGELOG.md](CHANGELOG.md)**.
>
> **Already have `dotnet-reflect@rlarch` / `dotnet-source@rlarch` installed?** Those were separate
> plugins and are gone. Follow **[upgrade_guide.md](upgrade_guide.md#upgrading-to-the-single-rlarch-plugin)**
> — it takes two minutes, and there is one scope trap in it that will bite you if you improvise.

## The nine skills

| Skill | Substrate | Use it for |
|---|---|---|
| [`dotnet-reflect`](#dotnet-reflect) | compiled DLLs | a **dependency you don't own** — signatures, docs, decompiled bodies, version diffs |
| [`dotnet-source`](#dotnet-source) | your `.cs` via Roslyn | the **.NET code you're editing** — search, outline (private included), find-usages, dead code |
| [`ts-source`](#ts-source) | your `.ts`/`.tsx` via the TS compiler API | the **frontend you're editing** — search, outline, JSX-aware find-usages, module graph, dead code |
| [`orchestrate`](#orchestrate) | your plan + subagents | an **undertaking that won't fit in one context** — delegate, verify, re-plan, commit |
| `controller` | a queue + nested Claude sessions | a **backlog** of large, mostly independent undertakings — sequence, brief, steer, verify, land |
| `nested-claude` | `claude --bg` | running **Claude Code inside Claude Code** — spawn, address, steer and watch background agents |
| `code-comments` | your code | a **subtractive comment pass** — the default verdict is DELETE; only a constraint the code can't state itself survives |
| [`workstate-setup`](#workstate) | Docker + `claude mcp add` | getting the **WorkState** server running and registered, so the `work_*` tools are callable |
| `workstate-import` | your repo's plans and docs + `work_import` | bringing a repo's **existing planning material** into WorkState — keeps only what is true of the current code, asks before writing, records source and reason |

### Prerequisites and platforms

Every skill runs on Windows, macOS and Linux; what differs is what has to be installed.

| Skill | Needs | Platform notes |
|---|---|---|
| `dotnet-reflect` | **.NET 10 SDK** | file-based C# scripts, same command everywhere |
| `dotnet-source` | **.NET 10 SDK** | Windows: `ds.ps1` needs **pwsh 7+**; macOS/Linux: `bash ./ds.sh` |
| `ts-source` | **Node 18+** | Windows: `tss.ps1` (pwsh 7+ or Windows PowerShell); macOS/Linux: `bash ./tss.sh` |
| `nested-claude` | Claude Code CLI; **Node 18+** for the `agents` tool (`agent_launch`/`agent_stop`/`agent_list`); Python 3 for the optional watchdog | the tool on every OS; fallback launchers: PowerShell on Windows, bash on macOS/Linux |
| `controller` | **WorkState**; **Node 18+** for the `agents` tool (or `nested-claude`'s launchers) | any OS with the tool; fallback: Windows (PowerShell) or macOS/Linux (bash) |
| `orchestrate` | **WorkState** | — |
| `code-comments` | nothing | — |
| `workstate-setup` | **Docker** (multi-arch images — Apple Silicon is fine) | Docker Desktop, or on Linux the Docker engine; macOS also Colima/OrbStack |

The two .NET skills are complementary, not competing: reflect reads *metadata* (public surface only,
needs a built DLL), source reads *your source* (sees private, works unbuilt). `ts-source` is
`dotnet-source`'s twin on the other side of the stack — same commands, same tiering, same output
shape, so the two halves of a full-stack repo are navigated the same way.

`controller`, `orchestrate` and `nested-claude` stack in that order: a controller runs a queue and
gives each item to a nested session, which usually runs `orchestrate` on it.

### On names

Skills that ship in a plugin are namespaced: typed as slash commands they are `/rlarch:dotnet-reflect`,
`/rlarch:dotnet-source`, `/rlarch:ts-source`, `/rlarch:orchestrate`, `/rlarch:controller`,
`/rlarch:nested-claude`, `/rlarch:code-comments`, `/rlarch:workstate-setup`. **You rarely need to
type them.** Automatic invocation is unaffected by the prefix — describe the task and Claude loads
the skill from its description. The namespace cannot be turned off; see the upgrade guide if you want
the trade-offs.

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

From `$CLAUDE_PLUGIN_ROOT/skills/dotnet-source`, with the launcher for your OS:

```powershell
# Windows — pwsh 7+
./ds.ps1 metrics --sort methods --top 20      # find the god-classes
./ds.ps1 outline AknPersistenceService        # 30 members — 19 of them private
./ds.ps1 find-usages WhereTenantRead          # 62 call-sites + the declaration
./ds.ps1 impls ITenantContext
./ds.ps1 discover --semantic                  # project set + reference health
```

```bash
# macOS / Linux — bash
bash ./ds.sh metrics --sort methods --top 20
bash ./ds.sh outline AknPersistenceService
bash ./ds.sh find-usages WhereTenantRead
bash ./ds.sh impls ITenantContext
bash ./ds.sh discover --semantic
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

From `$CLAUDE_PLUGIN_ROOT/skills/ts-source`, with the launcher for your OS:

```powershell
# Windows — pwsh or Windows PowerShell
./tss.ps1 metrics --sort loc --top 20         # find the files that got out of hand
./tss.ps1 outline --file src/utils/http.ts    # 42 declarations, most of them not exported
./tss.ps1 find-usages Badge                   # 106 JSX usages + 25 imports + the declaration
./tss.ps1 graph src/utils/http.ts --importers # blast radius of a change
./tss.ps1 dead                                # what can I delete?
```

```bash
# macOS / Linux — bash
bash ./tss.sh metrics --sort loc --top 20
bash ./tss.sh outline --file src/utils/http.ts
bash ./tss.sh find-usages Badge
bash ./tss.sh graph src/utils/http.ts --importers
bash ./tss.sh dead
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
| **The store is the memory, not context** | every decision, assumption, verification and result lands in WorkState the moment it happens, so a fresh session can resume from one `work_brief` call alone |
| **Red-checks** | any test defending a fix must be confirmed *failing* against the un-fixed code — the cheapest proof in the system, run in a context that's being thrown away anyway |
| **Reviews are rare and priced** | a review agent costs more than an extra worker task; there's an explicit risk dial for when to spend one, and a phase gate that looks only at the seams between tasks |
| **The plan is audited, not just executed** | every couple of phases a fresh agent checks the phases you *haven't* run yet — above all whether each task's verification command could actually go red, because a filter that already passes gets an unimplemented task accepted by every control in the system |

It also carries a hard ceiling on deliberation — an evidence budget per open question, a
reversibility test, and explicit escalation triggers — because rigor that never terminates isn't
rigor.

### The run lives in WorkState

`orchestrate` **requires** the [WorkState](#workstate) server; its first act is a `work_brief` call,
and if the `work_*` tools are missing it says so and points at `/rlarch:workstate-setup`. There is no
markdown fallback. Phase 0 writes the run into the store:

| What | Where |
|---|---|
| the goal paragraph, phases, tasks, done-criteria | a `run` item and its children (`work_create`, `work_link`) |
| the repo's binding facts and safety rules, written **once** | constraints (`work_constrain`), rendered into the BINDING block of every brief |
| the running record | reports, verifications, findings and notes, plus a journal that writes itself |

You approve the plan once, before the first writing agent runs. After that it runs to completion
unless an escalation trigger fires.

---

## WorkState

> **Only want `dotnet-reflect`, `dotnet-source`, `ts-source` or `code-comments`? Skip this section
> entirely.** WorkState is a **.NET service plus a Postgres, running in Docker on your own machine**,
> and nothing installs or starts it for you. It costs a container you have to keep alive, and the
> four navigation/cleanup skills never touch it. **`orchestrate` and `controller` require it** —
> since 2.x (first published as 2.2.0) they keep their plan and record nowhere else. `nested-claude` does not need it.

WorkState is an MCP server that holds **what coding agents are doing**: projects, runs, phases,
tasks, findings, claims, assumptions, the evidence behind each verification, and an append-only
journal of every change. It replaced the `PLAN.md` / `LEDGER.md` files an orchestrated run used to
scatter across a repo in 1.x — one store, queryable, with a generated recovery payload
(`work_brief`) that cannot go stale the way a hand-maintained ledger does.

The same port serves a read-only **operator dashboard**, so a human can watch a run happen instead of
reading a markdown file afterwards:

- an **overview** of every project and run, each run a bar with one segment per task, coloured by state;
- a **Needs you** inbox — everything waiting on a person, each row with the call that resolves it;
- the **run plan**, with item detail beside it: done-criteria, findings, notes, and *what the agent
  reported* next to *what was verified*;
- **activity** (the journal, grouped by day) and each project's **backlog** of deferred work;
- a dark and a light theme, following the system setting until you pick one.

Writes only ever go through the MCP tools. The dashboard is built on a **JSON read API v1**
(`/api/v1/*`), which other clients can use too — [docs/workstate/10-read-api.md](docs/workstate/10-read-api.md).
A session that sends an `X-WorkState-Agent` header is recorded **by name** on its claims and events —
[docs/workstate/11-sessions.md](docs/workstate/11-sessions.md).

Claude Code never manages the process — it only connects. You start it once:

```bash
docker compose -f mcp/compose.yml up -d
```

…and it is at **http://127.0.0.1:5290** permanently (`restart: unless-stopped`, so it survives a
reboot wherever Docker starts at logon). Use `127.0.0.1`, not `localhost`: the server binds IPv4
loopback only, and some clients try `::1` first.

**The way in is [`/rlarch:workstate-setup`](plugins/rlarch/skills/workstate-setup/SKILL.md).** It
works out which of four states your machine is in — already working, registered but down, up but not
registered, or nothing at all — and does only the missing part: finds or asks for this repo, writes
the local-only `mcp/.env` the stack needs, starts it, runs the one `claude mcp add`, and then proves
from `tools/list` how many tools actually came back. **It never clones anything without asking.**

The plugin's `.mcp.json` deliberately holds **no WorkState entry** — only the local `agents` stdio
server of `nested-claude`, which needs nothing but Node. A shipped WorkState entry is attempted by
every session in every project, so for everyone who installed `rlarch` to get `dotnet-reflect` it
would be a failed MCP connection, forever — and it would buy nothing, because MCP servers are
resolved at session start either way. If you prefer a config file to the `claude mcp add`, the exact
shape is committed at [`mcp/plugin.mcp.json`](mcp/plugin.mcp.json); copy it where you want it yourself.

The server's own design notes are in [`docs/workstate/`](docs/workstate/).

---

## The self-improvement loop

`orchestrate` critiques itself, and the critique is a normal repo workflow rather than a vibe.

1. **Retro.** At close-out, *only if the run earned it* — a rework, a re-plan, a red verification, a
   gate blocker, or one traceable ≥10k-token waste caused by the skill's own guidance — the skill
   files a GitHub issue against this repo, labelled `orchestrate-retro`, proposing **one** concrete
   edit to **one** of its own files. It's built from the store's record of the run, plus at most one
   read — the file it proposes to edit, so the before/after is verbatim. No recon, no review agent.
   A clean run teaches nothing and is skipped. It posts unasked **only where you have push access
   to this repo**; anyone else gets the same retro written to `.claude/retro/<run ref>/RETRO.md`
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
  .mcp.json                              # the `agents` stdio server only (no WorkState entry)
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
  skills/controller/
    SKILL.md
  skills/nested-claude/
    SKILL.md
    assets/{Start,Stop}-NestedAgent.ps1  # Windows launchers
    assets/{start,stop}-nested-agent.sh  # their macOS/Linux bash twins
    assets/watch-nested-agent.py         # the watchdog (stdlib Python 3)
    server/agents.mjs                    # the `agents` stdio MCP server (Node 18+, no dependencies)
    server/test/                         # node --test "plugins/rlarch/skills/nested-claude/server/**/*.test.mjs"
  skills/code-comments/
    SKILL.md
  skills/workstate-setup/
    SKILL.md                             # four states, one procedure — see "WorkState" above
  skills/workstate-import/
    SKILL.md                             # discover, judge against the code, ask, work_import
mcp/                                     # the WorkState server — NOT part of the plugin payload
  WorkState.slnx + WorkState.{Api,Core,Mcp,Tests}/
  compose.yml / compose.dev.yml / Dockerfile / .env.example
  plugin.mcp.json                        # reference config, deliberately not shipped in the plugin
docs/workstate/                          # the server's design notes
README.md
CHANGELOG.md
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
