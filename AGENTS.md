# AGENTS.md — working in this repo

This repo is a Claude Code **plugin marketplace** named `rlarch`, published at
`github.com/r-Larch/skills`: one plugin, `rlarch`, with four skills — `dotnet-reflect`,
`dotnet-source`, `ts-source` and `code-comments`. Manifests + Markdown + self-contained tools that
ship as source. **A change here needs a version bump** (next section) or it reaches nobody.

The planning and agent skills (`workstate`, `workstate-setup`, `workstate-import`, `controller`,
`orchestrate`, `nested-claude`) and the `agents` MCP server left in 3.0.0 for the `workman` plugin,
together with the Workman server, which is developed at `github.com/getsupraman/workman`. A
change to any of those belongs in the workman repo, not here.

```
.claude-plugin/marketplace.json     # the marketplace — ONE plugin entry
.claude/orchestration/, briefs/      # per-run artifacts (GITIGNORED — never write guidance here)
.github/assets/                     # README images (Workman marks are copied from the Supraman brand kit, never redrawn)
plugins/rlarch/
  .claude-plugin/plugin.json        # the single plugin — carries `version` (see below)
  skills/<name>/                    # one directory per skill: SKILL.md + its own scripts/, tool/, references/
tests/dotnet-source/                # tests for the dotnet-source tool
docs/dotnet-source-design.md        # design notes for the dotnet-source tool
README.md, CHANGELOG.md, upgrade_guide.md
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
| `README.md`, `docs/`, `tests/`, `.github/` | **none** — they do not ship through the plugin |

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
- **A `plugins/` change is its own commit**, with the `plugin.json` version bump in it.
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
- **Public repo.** Examples use neutral names (`MyApp`, `App.slnx`); never name a client, a
  company's internal project or a private host in a skill, a doc or a commit message.
- Nothing in the shipped tools executes user code: Roslyn and the TypeScript compiler API only ever
  parse and bind it. Keep it that way.

## Where the tools' own docs live

Each skill's `SKILL.md` carries its maintenance contract, including what to do when you are running
the *installed* copy under `~/.claude/plugins/cache/` (edits there are wiped by the next update, so
they must also become an issue or a PR). Read it before changing a tool.
