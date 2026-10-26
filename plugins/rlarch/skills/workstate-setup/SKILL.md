---
name: workstate-setup
description: "Get the WorkState MCP server running and registered, so the `work_*` tools are callable. WorkState is a self-hosted HTTP MCP server (a .NET service plus a Postgres, in Docker, on your own machine) that holds what coding agents are doing - projects, runs, phases, tasks, claims, evidence and an append-only journal - with an operator dashboard on the same port. This skill detects which of four states the machine is in and runs one shipped script that does only the part that is missing: writes the local-only `.env` with random tokens, pulls the published image (no clone needed) or builds from a clone, starts it, registers it with `claude mcp add`, and proves from `tools/list` that the tools are really there - without the model ever reading, pasting or curling a token. Also backs up and restores the database. USE FOR: `work_brief` or any other `work_*` tool is missing and a skill told you it needs the store; a first install of WorkState; \"the workstate tools are gone\"; the server is up but this session cannot see it; backing up or restoring WorkState. DO NOT USE FOR: using the tools once they work (call `work_brief`), or deploying WorkState anywhere but localhost."
---

# WorkState setup

**After running this, `work_brief` works — in the *next* session, not this one.** MCP servers are
resolved at session start; nothing you do here can add a tool to the session you are standing in.
Say that up front, or your own success reads as a failure.

WorkState is a server **you** run. Claude Code only ever connects to it. Everything below is one
script, shipped next to this file — `assets/workstate-setup.sh` (bash: macOS, Linux, WSL, Git Bash)
and `assets/workstate-setup.ps1` (PowerShell) — and your job is to pick its flags and read its status
lines. Find it with `echo "$CLAUDE_PLUGIN_ROOT"`: the scripts are at
`$CLAUDE_PLUGIN_ROOT/skills/workstate-setup/assets/`.

## What the model never does

The tokens and passwords live in one file, the install's `.env`, and in Claude Code's own config.
The script reads them and hands them to Docker and to `claude mcp add` itself; **you never see one,
and you keep it that way:**

- **never `Read`, `cat`, `grep` or open the `.env`**, and never write one yourself;
- **never run `claude mcp get workstate`** — it prints the registration's `Authorization` header in
  clear. Use `claude mcp list`: it shows the URL and the status and no header;
- never put a token on a command line, in a `curl`, or in a file you write.

The dashboard password is for the human: tell them it is in the `.env` the script names; do not print
it. Every line the script prints is safe to read — it was written and tested so that no value from
the `.env` appears in its output.

---

## 1. Which of the four states is this machine in? Say which, before running anything

**First question: can you call `work_brief` right now?** If `work_brief` **and** `work_constrain` are
in your tool list, this is the **already working** state. Say so, change nothing, and stop — unless
`work_brief` says *"session unnamed — /rlarch:workstate-setup installs the session helper"*: then
run the script with `--session-names` (§3) and nothing else.

`work_brief` there but **no `work_constrain`** is a fifth state: **registered and running, but older
than the skills.** Run the script with `--update` (§3); registration is left as it is.

Otherwise run these two — separately, and read both before deciding. Run the `curl` in the **Bash
tool** (Git Bash on Windows); Windows PowerShell 5.1's `curl` is `Invoke-WebRequest`.

```
claude mcp list
```
```
curl -s -m 5 -o /dev/null -w "%{http_code}" http://127.0.0.1:5290/health
```

| `workstate` line in `claude mcp list` | `/health` | State | Do |
|---|---|---|---|
| none | fails / not `200` | **nothing present** | §2, then §3 |
| present | fails / not `200` | **registered, server down** | §2, then §3 — the script keeps the registration |
| none | `200` | **server up, not registered** | §3 — it finds the `.env` already there and only registers |
| `✔ Connected` | `200` | **registered and healthy, this session just started too early** | §5 only — restart, and change nothing else |

That last row is the one worth reading twice: a `Connected` server with no `work_*` tool in your list
is not broken, it is a session that began before the registration did.

---

## 2. Which install: the WorkState home, or a clone

**The default needs no clone.** The script keeps everything in the **WorkState home**,
`~/.workstate/` (`%USERPROFILE%\.workstate` on Windows): a `compose.yml` that pulls the published
image `ghcr.io/r-larch/workstate-api`, and the `.env`. A machine needs Docker and nothing else.

Use **`--clone <path>`** instead when the machine already runs WorkState from a clone of
`github.com/r-Larch/skills` (a `<clone>/mcp/.env` exists), or the user develops WorkState: the script
then uses `<clone>/mcp/compose.yml`, which builds the image from source, and `<clone>/mcp/.env`.

**An existing clone install keeps `--clone`.** Both composes use the project name `workstate` and so
the same database volume — but the volume only accepts the Postgres password it was created with,
which is in the clone's `.env`. Moving such a machine to the home means the user copies
`<clone>/mcp/.env` to `~/.workstate/.env` first (a `cp`; nobody needs to read it). The script
refuses to write a new `.env` next to an existing database volume, so a wrong guess stops with
`env  … already has a database volume` instead of locking the API out of its data.

Never clone anything yourself: the home install does not need it, and a clone is a directory on
someone else's machine that they choose.

---

## 3. Run the script

From the project directory, in the Bash tool:

```
bash "$CLAUDE_PLUGIN_ROOT/skills/workstate-setup/assets/workstate-setup.sh"
```

or in PowerShell:

```
& "$env:CLAUDE_PLUGIN_ROOT/skills/workstate-setup/assets/workstate-setup.ps1"
```

| bash | PowerShell | Does |
|---|---|---|
| `--clone <path>` | `-Clone <path>` | use a clone (§2) instead of the home |
| `--home-dir <dir>` | `-HomeDir <dir>` | a different home than `~/.workstate` |
| `--role orchestrator\|controller\|worker` | `-Role …` | whose token is registered; **orchestrator is the usual choice** |
| `--agent <session>` | `-Agent <session>` | register at **local** scope (this project only) with `X-WorkState-Agent: <session>`, so sessions here are recorded by that name |
| `--name <name>` | `-Name <name>` | registration name (default `workstate`) |
| `--port <port>` | `-Port <port>` | host port, used only when it writes a **new** `.env` (default 5290) |
| `--image-version <tag>` | `-ImageVersion <tag>` | pin the image instead of `latest` |
| `--update` | `-Update` | pull the image again (home) or rebuild it (clone) |
| `--force` | `-Force` | replace an existing registration of the same name |
| `--session-names` | `-SessionNames` | re-register an existing install with the session helper, keeping its token's role |
| `--dry-run` | `-DryRun` | do everything but write the registration; show it with the token masked |
| `--backup <file>` / `--restore <file>` | `-Backup` / `-Restore` | §4, and nothing else |

The registration also runs `session-header.mjs` (copied next to the `.env`; needs `node`) on each
connect: it keys the session by its Claude Code process, so `work_session(name)` can name a
hand-started session that shares this registration. A new install gets it; an existing one needs
`--session-names` once, then a new session.

The roles nest — a controller token also satisfies orchestrator and worker. Register a worker only
for a session that just executes tasks handed to it. Sessions launched by `nested-claude`'s
`agent_launch` get their own minted token and name instead; give it `workstate.env_file` = the `.env`
this script names (`<home>/.env` or `<clone>/mcp/.env`) — a path, which is all it needs.

Each step prints one line; every step is idempotent, so running it again is always safe:

```
home      /home/u/.workstate (pull-only compose written)
env       wrote /home/u/.workstate/.env: 3 tokens, dashboard and Postgres passwords (values not shown, mode 600)
start     project 'workstate' up, http://127.0.0.1:5290/health 200
register  'workstate' -> http://127.0.0.1:5290/mcp as orchestrator (user scope; token stored by Claude Code, not shown)
prove     19 work_* tools for the orchestrator token: orchestrator/controller
dashboard http://127.0.0.1:5290 - sign in with WORKSTATE_DASHBOARD_USERNAME / _PASSWORD from /home/u/.workstate/.env
next      start a new Claude Code session: the work_* tools appear there, not in this one
```

An existing `.env` is **never overwritten** (`env  kept …`), and an existing registration is kept
unless you pass `--force`. **Report the `prove` count to the user** — the count *is* the role check,
because a tool a token may not call is absent from `tools/list` rather than refused:

| `prove` says | It means |
|---|---|
| **19** | an orchestrator or controller token, working |
| **6** | a worker token, working |
| another number | the running image is older or newer than these skills — run again with `--update` |
| an error naming the token or the server | read the line; it names the `docker compose … logs api` to look at |

When it stops instead, the last line says why. `docker  not reachable` means Docker itself is not
running: **ask the user to start Docker Desktop** (Linux: `sudo systemctl start docker`; macOS with
Colima or OrbStack: `colima start` or open OrbStack) and run it again — that is not something to
work around. `… is NOT gitignored` (clone only) means one `git add` would publish the tokens: stop
and tell the user, and do not edit `.gitignore` yourself.

The image is multi-arch (amd64 and arm64), so Apple Silicon needs nothing special, and
`restart: unless-stopped` brings the stack back wherever Docker starts at logon — you run this once,
not once per session. Every address is `127.0.0.1`, not `localhost`: the server binds IPv4 loopback
only, and some clients resolve `localhost` to `::1` first and find nothing there.

---

## 4. Back up and restore

The Postgres volume is the only copy of every plan, claim and verification. Back it up before
anything risky (an upgrade, a restore, moving machines):

```
bash "$CLAUDE_PLUGIN_ROOT/skills/workstate-setup/assets/workstate-setup.sh" --backup ~/workstate-2026-10-26.dump
```

It runs `pg_dump -Fc` inside the running `db` container (found by compose project, `--project` if not
`workstate`) and copies the dump out; no password passes anywhere. `--restore <file>` first writes a
safety dump of the current database to `<home>/backups/`, stops the API, runs
`pg_restore --clean --if-exists`, and starts the API again — **it replaces the data**, so confirm
with the user before you run it. Pass `--clone <path>` for a clone install, as everywhere else.

---

## 5. Restart — and say so

You cannot finish this yourself — the last step is the user's. **Tell them, in plain words, that the
`work_*` tools are registered and will appear in their next session, not this one.** Ask them to:

1. Exit this session and start a new one in the same project.
2. Call `work_brief` — it is the recovery payload and it needs nothing read before it. On an empty
   server it answers with onboarding instead of an error. Projects are bound to repositories, one
   project per repo: `work_brief(repo: {remote, root})` opens the repo's project, and onboarding
   names the `work_create` that binds an unbound one.

If the new session still has no `work_*` tool, the registration did not land: run `claude mcp list`
from that project — a `--agent` (local-scope) entry made in another directory does not follow you.
