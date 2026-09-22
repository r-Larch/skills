---
name: workstate-setup
description: >-
  Get the WorkState MCP server running and registered, so the `work_*` tools are callable. WorkState
  is a self-hosted HTTP MCP server (a .NET service plus a Postgres, in Docker, on your own machine)
  that holds what coding agents are doing — projects, runs, phases, tasks, claims, evidence and an
  append-only journal — with an operator dashboard on the same port. This skill detects which of four
  states the machine is in and does only the part that is missing: finds or asks for the repo that
  carries the stack, writes the local-only `mcp/.env` it needs, starts it, registers it with
  `claude mcp add`, and proves from `tools/list` that the tools are really there. USE FOR:
  `work_brief` or any other `work_*` tool is missing and a skill told you it needs the store; a first
  install of WorkState; "the workstate tools are gone"; the server is up but this session cannot see
  it. DO NOT USE FOR: using the tools once they work (call `work_brief`), or deploying WorkState
  anywhere but localhost.
---

# WorkState setup

**After running this, `work_brief` works — in the *next* session, not this one.** MCP servers are
resolved at session start; nothing you do here can add a tool to the session you are standing in.
Say that up front, or your own success reads as a failure.

WorkState is a server **you** run. Claude Code only ever connects to it. The plugin ships *skills*,
not the service — a marketplace install puts this file in a cache and puts no `compose.yml` on the
machine, so **do not assume the repo is here** (§2).

---

## 1. Which of the four states is this machine in? Say which, before running anything

**First question: can you call `work_brief` right now?** If a `work_*` tool is in your tool list,
this is the **already working** state. Say so, change nothing, and stop. Do not re-register a server
that is answering you.

Otherwise run these two — separately, and read both before deciding:

```
claude mcp get workstate
```
```
curl -s -m 5 -o /dev/null -w "%{http_code}" http://localhost:5290/health
```

| `claude mcp get workstate` | `/health` | State | Do |
|---|---|---|---|
| `No MCP server named "workstate"` | fails / not `200` | **nothing present** | §2 → §3 → §4 → §5 → §6 → §7 |
| prints the server | fails / not `200` | **registered, server down** | §2 → §3 → §4 → §6 → §7 |
| `No MCP server named "workstate"` | `200` | **server up, not registered** | §5 → §6 → §7 |
| prints `Status: ✔ Connected` | `200` | **registered and healthy, this session just started too early** | §7 only — restart, and change nothing else |

That last row is the one worth reading twice: a `Connected` server with no `work_*` tool in your list
is not broken, it is a session that began before the registration did.

---

## 2. Find the repo — and **ask before cloning**

The stack lives in `github.com/r-Larch/skills` under `mcp/`. Resolve it in this order and stop at the
first that works:

1. **A path the user gives you.** Ask for one if you are unsure — it is one question and it is
   cheaper than a wrong guess.
2. **The repo this session is already in**, if `mcp/compose.yml` exists in it. Check with `Glob`.
3. **Offer to clone** — and only offer:

   > I can't find `mcp/compose.yml` on this machine. Where would you like
   > `git clone https://github.com/r-Larch/skills` to put it?

   Once they name a directory, and not before:

   ```
   git clone https://github.com/r-Larch/skills <the directory they named>
   ```

**Never clone without asking, and never pick the directory yourself.** The repo is small — about
3 MB — so the reason is not bandwidth: it is a directory on someone else's machine that they have
to find, remember and live with afterwards.

Every command below runs **from the repo root** — the directory holding `mcp/`.

---

## 3. `mcp/.env` — required, local-only, and it must be ignored

The stack refuses to start without it: `compose.yml` uses `${VAR:?...}` for the Postgres password,
the three bearer tokens and the dashboard credential, so a missing value is a startup error, not a
default.

If `mcp/.env` already exists, **leave it alone** and go to §4. Otherwise write it with `Write` —
generating a different random string for each `<random>`:

```
WORKSTATE_WORKER_TOKEN=local-worker-<random>
WORKSTATE_ORCHESTRATOR_TOKEN=local-orchestrator-<random>
WORKSTATE_CONTROLLER_TOKEN=local-controller-<random>

WORKSTATE_DASHBOARD_USERNAME=operator
WORKSTATE_DASHBOARD_PASSWORD=local-dashboard-<random>

WORKSTATE_API_PORT=5290

POSTGRES_DB=workstate
POSTGRES_USER=workstate
POSTGRES_PASSWORD=local-<random>
```

**Say plainly what these are:** throwaway values for a service bound to localhost. They are not
secrets and they do not need a password manager. They exist because the stack is fail-closed by
design, not because anything here is exposed.

Then prove the file cannot be committed:

```
git check-ignore -v mcp/.env
```

It must print a rule (`.gitignore:21:.env	mcp/.env`). **If it prints nothing, stop and tell the
user** — the file is tracked or the ignore rule is gone, and one `git add` would put three tokens and
a password on a public repo. Do not continue, and do not "fix" it by editing `.gitignore` yourself.

---

## 4. Start the stack

```
docker compose -f mcp/compose.yml up -d
```

First run builds the API image and takes a few minutes. If it answers *cannot connect to the Docker
daemon*, Docker itself is not running — **ask the user to start Docker Desktop** and try again; that
is not something to work around.

Then check it came up healthy:

```
docker compose -f mcp/compose.yml ps
```

Both `api` and `db` should read `Up … (healthy)`. Confirm from outside the container too:

```
curl -s -m 5 -o /dev/null -w "%{http_code}" http://localhost:5290/health
```

`200` and you are done here. `restart: unless-stopped` is set on both services, so this survives a
reboot wherever Docker starts at logon — you run this once, not once per session.

The operator dashboard is the same address in a browser: **http://localhost:5290**, signed in with
the dashboard username and password from `mcp/.env`.

---

## 5. Register the server

Read the token you need out of `mcp/.env`. There are three, and they nest — a controller token also
satisfies orchestrator and worker:

| Token | Gives | Register this one if |
|---|---|---|
| `WORKSTATE_WORKER_TOKEN` | 6 tools — read, report, verify | the session only executes tasks handed to it |
| `WORKSTATE_ORCHESTRATOR_TOKEN` | 18 tools | **the usual choice** — you plan, close and judge |
| `WORKSTATE_CONTROLLER_TOKEN` | 18 tools | you run `/rlarch:controller` over a queue |

```
claude mcp add --scope user --transport http workstate http://localhost:5290/mcp --header "Authorization: Bearer <token>"
```

Two things about that line, both measured:

- **`--header` must come last.** It is variadic (`<header...>`) and it swallows every argument that
  follows it, silently — you get a registration that looks fine and a server that never authorises.
- **`--scope user`** registers it once for every project on the machine. Project scope means
  re-doing this in the next repo.

Tools then appear as `mcp__workstate__work_*`.

---

## 6. Prove it — from evidence, not from the absence of an error

Ask the server what it will actually give that token:

```
curl -s -m 10 -X POST http://localhost:5290/mcp -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" -H "Authorization: Bearer <token>" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' -o tools.json
```

**Keep the body in single quotes.** The `-d "{\"jsonrpc\":…}"` form you would reach for on Windows
fails in PowerShell — the argument is split and curl exits 3, *URL malformed*, which reads like a
server problem and is not one. Single quotes are correct in both bash and PowerShell.

The response is one very long line, so do not read it. Count what came back with `Grep`
(pattern `"name":"work_[a-z_]+"`, `-o`, output mode `content`) — one line per tool, so the line
count is the answer. **Report the number to the user.**

| You see | It means |
|---|---|
| **18** names | an orchestrator or controller token, working |
| **6** names | a worker token, working |
| `HTTP 401` / `AUTH_HEADER_REJECTED` | the token you sent is not the one the running container holds — you mistyped it, or `mcp/.env` was edited after the stack came up. Re-run §4's `up -d`; it recreates the container with the current values |
| nothing, connection refused | the server is not up; back to §4 |

A tool an agent may not call is simply **absent** from this list rather than refused, so the count
*is* the role check.

`tools.json` is scratch and it is **not** gitignored. Never `git add` it. Say it is there so the user
can delete it; do not delete files on their behalf.

---

## 7. Restart — and say so

You cannot finish this yourself — the last step is the user's. **Tell them, in plain words, that the
`work_*` tools are registered and will appear in their next session, not this one.** Ask them to:

1. Exit this session and start a new one in the same project.
2. Call `work_brief` — it is the recovery payload and it needs nothing read before it. On an empty
   server it answers with onboarding instead of an error.

If the new session still has no `work_*` tool, the registration did not land: re-run
`claude mcp get workstate` and read the `Scope` line — a project-scoped entry written in another
directory will not follow you.
