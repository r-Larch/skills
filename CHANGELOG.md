# Changelog

What changes for someone who has the `rlarch` plugin installed, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are the `version` field in
`plugins/rlarch/.claude-plugin/plugin.json`, which is what the updater gates on.

## [Unreleased]

## [3.0.1] - 2026-11-01

### Changed

- `dotnet-reflect`, `dotnet-source` and `ts-source`: examples and measurements use neutral project
  names (`MyApp`, `App.slnx`, "a 21-project production solution"). No behaviour change.

### Removed

- The repo no longer carries the Workman server source or its design docs. Workman is developed at
  [github.com/getsupraman/workman](https://github.com/getsupraman/workman); running servers and
  their data are not affected.

## [3.0.0] - 2026-11-01

### Removed

- **Six skills moved to the `workman` plugin**, and the `agents` MCP server (`plugins/rlarch/.mcp.json`)
  moved with them. They are invoked under new names:

  | rlarch 2.x | workman |
  |---|---|
  | `/rlarch:workstate` | `/workman:plan` |
  | `/rlarch:workstate-setup` | `/workman:setup` |
  | `/rlarch:workstate-import` | `/workman:import` |
  | `/rlarch:controller` | `/workman:controller` |
  | `/rlarch:orchestrate` | `/workman:orchestrate` |
  | `/rlarch:nested-claude` | `/workman:agents` (tools now `mcp__plugin_workman_agents__*`) |

  Install it with `/plugin marketplace add getsupraman/workman` and
  `/plugin install workman@supraman`. rlarch keeps `dotnet-reflect`, `dotnet-source`, `ts-source`
  and `code-comments`.
- Your Workman server, its `work_*` tools and your data are unchanged.

## [2.0.0 – 2.8.2] - 2026-10

The 2.x line added the Workman server and the planning, orchestration and agent skills that now ship in
workman; their history continues in the
[workman repo](https://github.com/getsupraman/workman). Changes to the skills that stayed:

- **2.2.1** — `dotnet-source` on macOS/Linux: the `serve` daemon survives its caller (own session,
  ignores SIGHUP), and a killed daemon no longer blocks later `serve` calls — the single-instance
  lock is now a kernel `flock`. Verified on Linux (WSL).
- **2.2.0** — macOS/Linux: `ds.sh` and `tss.sh` are executable and `.gitattributes` pins script line
  endings; `dotnet-source` and `ts-source` document the `bash ./x.sh` form and use the OS's temp
  directory. `dotnet-reflect` decompiles a generic type by its reflection name.

## [1.3.0] - 2026-09-10

- Added `code-comments` (generalised from the earlier .NET-only comment pass).

## [1.1.0] - 2026-09-02

- Added `ts-source`: `dotnet-source`'s twin for TypeScript/JavaScript.

## [1.0.0] - 2026-08-06

- One plugin, `rlarch`, replaces the separate `dotnet-reflect` and `dotnet-source` plugins.
  Migration: [upgrade_guide.md](upgrade_guide.md#upgrading-to-the-single-rlarch-plugin).
- Before 1.0.0, `dotnet-reflect` and `dotnet-source` were published as two separate plugins.
