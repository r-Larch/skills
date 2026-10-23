# Changelog

What changes for someone who has the `rlarch` plugin installed, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are the `version` field in
`plugins/rlarch/.claude-plugin/plugin.json`, which is what the updater gates on.

## [2.2.0] - 2026-10-23

The first published 2.x. It includes everything that was numbered **2.0.0** and **2.1.0** on the
`feat/workstate` branch; neither of those was ever published, so 1.5.0 → 2.2.0 is one step for an
installed user. Upgrading: **[upgrade_guide.md](upgrade_guide.md#upgrading-from-1x-to-2x)**.

### Breaking

- **`orchestrate` and `controller` require the WorkState server** (a .NET service plus Postgres,
  running in Docker on your machine). They keep their plan, binding rules, evidence and record in it
  and nowhere else. There is **no markdown fallback**: `PLAN.md` / `LEDGER.md` are no longer written,
  and without the `work_*` tools both skills stop and point at `/rlarch:workstate-setup`.
- `orchestrate`'s `assets/` templates (ledger and conventions) are removed; the store owns those
  shapes now.

### Added

- **WorkState** (`mcp/`), the MCP server the two skills record their work in: projects, runs,
  phases, tasks, findings, follow-ups, assumptions, constraints, claims, notes, verifications and an
  append-only journal. Closing an item is guarded: `work_status(done)` is refused until every
  done-criterion is checked, a verification not reported by a worker exists, no child is open and no
  blocker targets it. `work_brief` is a generated recovery payload, so a fresh session resumes from one call.
  Three roles by bearer token (worker, orchestrator, controller); claims are per working tree.
- **Operator dashboard** on the same port (http://127.0.0.1:5290): an overview of every project and
  run, a "needs you" inbox, the run plan with item detail (what the agent reported next to what was
  verified), activity, and each project's backlog. Read-only; dark and light theme.
- **JSON read API v1** (`GET /api/v1/*`, plus `/api/stream` for live updates), frozen and additive
  only; reachable with the dashboard cookie, any agent token, or an optional read-only reader token.
  See [docs/workstate/10-read-api.md](docs/workstate/10-read-api.md).
- **Sessions by name**: a session that sends an `X-WorkState-Agent: <name>` registration header is
  recorded by that name on its claims and events. See
  [docs/workstate/11-sessions.md](docs/workstate/11-sessions.md).
- **`/rlarch:workstate-setup`**: works out whether WorkState is already working, registered but down,
  up but not registered, or absent, and does only the missing part — writes the local-only
  `mcp/.env`, starts the stack, runs the one `claude mcp add`, and proves from `tools/list` how many
  tools came back. It never clones anything without asking.
- **`nested-claude` bash launchers** (`start-nested-agent.sh`, `stop-nested-agent.sh`), twins of the
  PowerShell ones. Both twins take a dry-run switch (`-DryRun` / `--dry-run`) that validates and
  prints the exact command without running `claude`. The launcher defaults to auto mode, and
  `-Watch` picks the right form for the OS.

### Changed

- `controller` and `nested-claude` claim each unit with its own worktree.
- Every WorkState address is `http://127.0.0.1:5290`, not `localhost`: the server binds IPv4
  loopback only, and some clients try `::1` first.
- The Docker compose file publishes the API on `127.0.0.1` only by default.
- WorkState server version 0.4.0.

### Fixed

- macOS / Linux: `ds.sh` and `tss.sh` are executable, and `.gitattributes` pins script line endings;
  `dotnet-source` and `ts-source` document the `bash ./x.sh` form and use the OS's temp directory.
- `dotnet-reflect`: decompiling a generic type by its reflection name.
- The plugin no longer ships a compiled `.pyc`.

## [1.5.0] - 2026-09-11

- Added `controller`: run a queue of large, mostly independent undertakings, each through a nested
  Claude session.

## [1.4.0] - 2026-09-10

- `nested-claude` gains a watchdog and tool profiles.

## [1.3.0] - 2026-09-10

- Added `code-comments` (generalised from the earlier .NET-only comment pass).

## [1.2.0] - 2026-09-10

- Added `nested-claude`: run Claude Code inside Claude Code.

## [1.1.0] - 2026-09-02

- Added `ts-source`: `dotnet-source`'s twin for TypeScript/JavaScript.

## [1.0.0] - 2026-08-06

- One plugin, `rlarch`, replaces the separate `dotnet-reflect` and `dotnet-source` plugins and adds
  `orchestrate`. Migration: [upgrade_guide.md](upgrade_guide.md#upgrading-to-the-single-rlarch-plugin).
- Before 1.0.0, `dotnet-reflect` and `dotnet-source` were published as two separate plugins.
