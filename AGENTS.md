# AGENTS.md — working in this repo

A Claude Code **plugin marketplace** named `rlarch`, published at `github.com/r-Larch/skills`.
No application code, no test suite. The product is manifests + Markdown + self-contained tools that
ship as source.

```
.claude-plugin/marketplace.json     # the marketplace — ONE plugin entry
.claude/commands/apply-retro.md     # repo-local maintainer command (committed)
.claude/orchestration/              # per-run artifacts (GITIGNORED — never write guidance here)
plugins/rlarch/
  .claude-plugin/plugin.json        # the single plugin — carries `version` (see below)
  skills/dotnet-reflect/            # SKILL.md + scripts/*.cs
  skills/dotnet-source/             # SKILL.md + ds.ps1/ds.sh + tool/   (.NET, compiled once to cache)
  skills/ts-source/                 # SKILL.md + tss.ps1/tss.sh + tool/ (plain .mjs, no build)
  skills/orchestrate/               # SKILL.md + references/ + assets/
README.md, upgrade_guide.md
```

## Shipping a change — the version bump is not optional

**Every change that must reach an installed user requires a bump of `version` in
`plugins/rlarch/.claude-plugin/plugin.json`. Pushing the commit is not enough.**

The updater gates on that field and caches the payload **per version** at
`~/.claude/plugins/cache/rlarch/rlarch/<version>/`. Without a bump, `/plugin` answers
*"rlarch is already at the latest version (X.Y.Z)"*, `/skills` reports *"No changes"*, and the commit
reaches nobody — even though the marketplace clone has already fetched it. The installed record in
`~/.claude/plugins/installed_plugins.json` keeps its old `gitCommitSha` and nothing signals a problem.

This has bitten this repo once already: `ts-source` was merged and pushed to `master`, the marketplace
clone fetched it, and the plugin stayed three commits behind with three skills instead of four.

| Change | Bump |
|---|---|
| New skill, new command, new capability | **minor** — `1.0.0` → `1.1.0` |
| Fix, wording, doc correction inside a skill | **patch** — `1.1.0` → `1.1.1` |
| Removing a skill, renaming one, breaking a documented flag | **major** |

> Older notes in this repo say *"no version bump needed — every commit is picked up as an update"*
> (see `docs/dotnet-source-design.md` › Packaging). That was true when `plugin.json` had **no**
> `version` field. One was added during the one-plugin consolidation, which silently switched updates
> from SHA-based to version-gated. Treat any surviving copy of that claim as a bug and fix it.

Release checklist:

```bash
claude plugin validate . --strict                 # marketplace manifest
claude plugin validate plugins/rlarch --strict     # plugin manifest
# bump plugins/rlarch/.claude-plugin/plugin.json  -> "version"
git commit && git push origin master
# verify as a consumer:
claude plugin marketplace update rlarch && claude plugin details rlarch   # skill count must be right
```

`--strict` turns warnings into a non-zero exit. Read the actual result line.

## Conventions

- **Branch, then PR into `master`.** Do not commit straight to it.
- **Adding a skill:** drop it in `plugins/rlarch/skills/<name>/` with a `SKILL.md`. A plugin
  auto-discovers `skills/`, `commands/`, `agents/`, `hooks/hooks.json`, `.mcp.json` — none are
  declared in `plugin.json`. **But the `version` still has to move, and the descriptions in
  `plugin.json` + `.claude-plugin/marketplace.json` + `README.md` must all mention it.**
- Plugin skills are **always namespaced** `/<plugin>:<skill>`; there is no way to make them bare.
  `skillDirectories` does not exist — do not write docs claiming otherwise.
- `$CLAUDE_PLUGIN_ROOT` resolves to the plugin's install dir; in-plugin paths are
  `$CLAUDE_PLUGIN_ROOT/skills/<skill>/...`.
- **Do not invent Claude Code settings, manifest fields, or slash commands.** Verify against
  `claude <cmd> --help` or the installed binary, or omit the claim.
- `bin/`, `obj/` and `.claude/orchestration/` are gitignored. Never stage them, and never put durable
  guidance in `.claude/orchestration/` — it is per-run and invisible to the next agent.
- Nothing in the shipped tools executes user code: Roslyn and the TypeScript compiler API only ever
  parse and bind it. Keep it that way.

## Where the tools' own docs live

Each skill's `SKILL.md` carries its maintenance contract, including what to do when you are running
the *installed* copy under `~/.claude/plugins/cache/` (edits there are wiped by the next update, so
they must also become an issue or a PR). Read it before changing a tool.
