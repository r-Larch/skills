# Changelog

What changes for someone who has the `rlarch` plugin installed, newest first. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are the `version` field in
`plugins/rlarch/.claude-plugin/plugin.json`, which is what the updater gates on.

## [Unreleased]

### Changed

- WorkState server **1.1.2** (no plugin change; one migration): one parent-kind rule.
  `work_create`, `work_move` and `work_import` share the same check. Only a project is top-level,
  and a feature with no parent is refused. A task may now sit under a task (migration
  `TaskUnderTask`). Every `work_assume` call that `work_threads`, attention and `work_report` print
  works when copied as is: an ambiguous bare ref like `A1` is printed run-qualified. Closing a
  question takes its answer (`work_status(question, done)` records it as a human verification and a
  decision note). The dashboard shows the question as Answered, and it no longer counts as closed
  without proof. On a phone the dashboard shows the page first, with the sidebar behind a Menu button.
- WorkState server **1.1.1** (no plugin change; one migration): fixes found while building 1.1.0. A
  claim made without an agent session belongs to its user, so another user with the same role name
  can no longer retake or extend it. `work_move` refuses a parent the item's kind doesn't allow: a
  project stays a root, and a task sits under a phase or a run. A workspace member sees and changes
  only their own agent sessions (`/api/v1/sessions`, the dashboard's Release, and
  `/api/agent-sessions/{id}`); someone else's answers 404 instead of 403. Before, any member could
  release any session from the Sessions page. Admins and credentials with no user still see every
  session.
- WorkState server **1.1.0** (no plugin change; four migrations): private and shared projects. A
  project's work is visible only to its members, which Postgres row-level security enforces; a
  non-member gets the same "not found" as for a ref that doesn't exist. Every existing project
  starts with every member of its workspace as a member. New projects are private to their creator.
  The overview marks each project Private, Shared with N, or Open to the workspace, and a project's
  Members panel adds people (one at a time, or everyone) and removes them. Repo folder paths
  (`work_brief(repo:{root})`) and path tree locks are now per user, so two people with the same path
  on different machines no longer collide; repo remotes stay shared. `/api/stream` now carries the
  `X-WorkState-Api` header. Back up first.
- WorkState server **1.0.1** (no plugin change; one migration): projects and runs get unique refs.
  A run created without a ref is numbered `1`, `2`, … under its project (every one was `1` before),
  a project `project-1`, `project-2`, …, and a second run or project with a ref its siblings already
  hold is refused. The migration renames each existing duplicate but the oldest to `<ref>-2`, …, and
  journals the rename. Back up first. OAuth refresh tokens are now 43-character opaque handles: 1.0.0's
  self-contained ones (about 2900 characters) no longer fit Windows Credential Manager, which broke
  Foreman's refresh. A refresh token 1.0.0 issued still redeems once, into a handle.
- WorkState server **1.0.0** (no plugin change; seven migrations): one server, several
  tenants. The dashboard has users, tenant memberships and a system administrator; the configured
  dashboard credential creates the first user at its first sign-in and is not read after that.
  Every request acts in its sign-in's tenant only, enforced in the queries and by Postgres row-level
  security. OAuth tokens and minted agent sessions belong to a user and one tenant; the consent page
  names the tenant. New: admin pages (workspaces, members, copyable invite links), a workspace
  switcher, password change and admin reset links, and a Ctrl+P command palette that finds any ref
  across projects. A server deploy accepts no static role token; a local install keeps them. After
  upgrading, sign in to the dashboard once; existing OAuth connections carry over on their next
  refresh.
- WorkState server **0.9.2** (no plugin change, no migration): the consent page lets the browser
  return to such a client's actual port (its `form-action` blocked the redirect, so Approve hung) and
  no longer warns that the address is unregistered.
- WorkState server **0.9.1** (no plugin change, no migration): an app that registers itself with a
  `http://127.0.0.1/callback` (or `localhost`) callback may sign in on any port of that host, as RFC
  8252 §7.3 requires. Foreman and `agent_signin` were refused with `ID2043` before; Claude Code was
  not affected.
- WorkState server **0.9.0** (no plugin change): OAuth on the read API, the stream and the
  agent-session mint for apps like Foreman (scopes `workstate.read`, `workstate.sessions`), signing
  an app out ends the sessions it minted, a launcher can take over a session name with the same
  Claude session id, and the dashboard's new Sessions page releases a stuck name. Rebuild or
  `--update` to get it; one additive migration (`OAuthMintedSession`). Back up first.

## [2.8.1] - 2026-10-29

### Fixed

- **`workstate-setup`: the pulled server reads `WORKSTATE_PUBLIC_URL` and `WORKSTATE_TRUSTED_PROXIES`.**
  The compose file the setup copies to `~/.workstate/compose.yml` didn't pass them to the server, so
  setting them in `~/.workstate/.env` changed nothing: the `web` line kept the request's host, and a
  reverse proxy's `X-Forwarded-*` headers weren't believed. It now passes both, as `mcp/compose.yml`
  does. The next run of the setup replaces the copied file.

## [2.8.0] - 2026-10-28

### Added

- **`agent_signin`: sign in once, no `.env` for `agent_launch`.** Run `agent_signin` (default
  `http://127.0.0.1:5290`, or `{url}` for another WorkState server) once: it opens your browser on
  the WorkState sign-in page, you sign in with the dashboard user and approve, and from then on
  `agent_launch` and `agent_stop` mint and revoke each agent's token with that grant. The grant
  lives in `~/.workstate/grants.json` (`0600` on macOS and Linux; on Windows your user profile's
  ACL protects it) and never appears in a tool result. It is refreshed as needed and lasts 30 days
  unused. A launch without a grant, or with an expired one, is refused with the `agent_signin`
  call to make. Needs WorkState server 0.9.0 or later.
- **`agent_signout`** revokes the grant at the server and deletes it. Every agent session minted with
  it stops working at its next request.

### Changed

- **`workstate.env_file` is optional.** Leave it out once you have signed in. Pass it for headless
  use (CI, a machine nobody can sign in on): it works exactly as before and wins over a stored
  grant.

## [2.7.0] - 2026-10-28

### Added

- **`workstate-setup` registers with OAuth sign-in by default.** When the running server offers it
  (its `/.well-known/oauth-protected-resource/mcp` document answers), the script registers
  `http://127.0.0.1:<port>/mcp` with no `Authorization` header and stores no token. In the next
  session you run `/mcp`, pick the server, choose *Authenticate*, sign in with the dashboard user
  from the `.env` and approve. The sign-in gets the controller toolset. The `prove` step checks
  what Claude Code needs to find the sign-in: `POST /mcp` without a token answers 401 with
  `resource_metadata=`, and that document answers. The session helper stays on OAuth
  registrations, and `--agent`, `--name`, `--force`, `--session-names` and `--dry-run` work as
  before. Needs a WorkState server image with OAuth; against an older one the script says so and
  falls back to a token.
- **`--auth oauth|token`** (`-Auth` in PowerShell) chooses the kind explicitly. A new `auth` status
  line says which kind was chosen and why. `--auth oauth` against a server without OAuth stops and
  points at `--update`. `--role` with `--auth oauth` stops, because an OAuth sign-in has no role
  to choose.

### Changed

- **`--role` now implies a token registration**, which works exactly as before, `tools/list` count
  included. An existing registration is recognised as OAuth or token and keeps its kind, even with
  `--force` or `--session-names`, unless `--auth` or `--role` says otherwise. Sessions started by
  `agent_launch` keep using tokens through `workstate.env_file`, since they cannot sign in in a
  browser.
- WorkState server **0.8.0**: OAuth sign-in, the sign-in and consent pages, trusted proxies.
  Rebuild or `--update` to get it; one additive migration (`OAuthServer`). Back up first.

## [2.6.0] - 2026-10-27

### Added

- **`/rlarch:workstate`**: the planning skill. "Use /rlarch:workstate to plan feature XY" writes a
  run into WorkState (units or phases with done-criteria and a verify command each, binding rules,
  assumptions, ordering, open questions), shows it and stops. "Propose what we should build next"
  ranks 3 to 5 candidates from the board and the repo. "Triage" proposes closes, defers, merges
  and reorders and applies only the ones you approve. It never writes code or starts an agent.

### Changed

- **`controller` and `orchestrate` take an approved plan as approved.** After "approved, execute as
  /rlarch:controller" (or orchestrate), they record the approval on the run, skip their own planning
  and approval gate, and start. The plan audit and unit classification still run.

## [2.5.1] - 2026-10-27

### Added

- **Readable dashboard links.** `work_brief` (every variant, the project and gate briefs included)
  and `work_get` (per item) print a `web` line, `<base>/r/<project/run/ref>`, that a session can
  hand a human. The server's `/r/{path}` resolves any ref form `work_get` accepts and redirects to
  that item's dashboard page. Ambiguous or unknown paths get a page that lists the candidates. It is
  behind the dashboard sign-in, and after signing in you land on the linked page. The base is the
  request's host unless `WORKSTATE_PUBLIC_URL` is set (optional, in `mcp/.env.example`). Needs the
  WorkState server image with this change.

### Changed

- **Dashboard: the run page is a split.** The plan list is the page: it scrolls with the page, and
  the run header scrolls away with it. The item detail is a panel docked on the right at full
  height with its own scroll, resizable by dragging its edge or with the arrow keys on the focused
  edge (Home or a double-click resets), and the width is remembered per browser. When the run area
  is narrower than 769px the detail opens as a sheet over the list, with a title row and a Close
  (or Escape) that clears the selection and keeps the filter. Selecting an item keeps the list where
  it was.

### Fixed

- **Dashboard: one scrollbar on every page.** The copy buttons' screen-reader status text was
  positioned against the document instead of the scrolling page, so a long page also grew the
  document and showed a second scrollbar beside the page's own.

## [2.5.0] - 2026-10-27

### Added

- **`work_brief(scope:"<phase>", for:"gate")`** — the phase's tasks with their status and last
  verification, the seams computed from `work_link` (edges between two of its tasks, and edges that
  cross its boundary), and the calls that record the verdict: `work_verify` + `work_status(done)` to
  pass, `work_finding(source:"gate", severity:"blocker")` to fail. Read-only. The attention line
  *"all N tasks done, gate not run"* now routes to it instead of to a plain resume brief.
- **`work_search` finds assumptions, rules and notes, and takes an optional `scope`.** Until now it
  searched item titles and intents only, so a rule (`C12`), an assumption (`A1`) or a note could be
  found only by someone who already knew where it was. Each new hit names its kind and prints the
  call that opens it: `work_get` for an assumption (qualified, `run-a/A1`), the item's worker brief
  or the conventions for a rule, `work_history` on the note's item. `scope:"<run, project or item>"`
  keeps only hits inside that subtree. Workspace-wide rules and assumptions are left out of a scoped
  search, since they belong to no item. A search without `scope` prints its item rows exactly as
  before. A new index (`note_search_idx`) keeps note matching off a sequential scan.
- **The gate brief prints the phase's gate criterion.** When a phase records what must be true to
  accept it (`gate_criterion`, today set by the importer), the gate brief's header shows it as a
  `PASS IF` line. A phase without one shows nothing extra.
- **The dashboard API serves the gate brief.** `GET /api/v1/items/{id}/brief?for=gate` returns the
  same text as `work_brief(for:"gate")`. Any value other than `resume`, `worker` or `gate` is still a 400.
- **`workspace/A1` names an assumption that sits on no item.** Its bare `A1` was ambiguous as soon as
  a run had its own `A1`, and nothing else named it. `work_search` and `work_get` now print
  `workspace/A1` in that case, and `work_get` and `work_assume` accept it.

### Changed

- **`tools/list` budgets raised, and four descriptions trimmed** (owner's decision, 2026-10-27). A
  keyed orchestrator list had reached 6,395 of its 6,400 tokens, so the next tool would not fit.
  The ceilings are now 2,600 for a worker and 7,200 for an orchestrator or controller. `work_brief`,
  `work_get`, `work_threads` and `work_status` each lost one sentence that only repeated the server
  instructions or another line of the same description. Lists now measure 1,963 for a worker and
  6,155 for an orchestrator or controller (2,111 and 6,303 with `work_session`).

### Fixed

- **Dashboard: the run plan opens on Open, progress names cancelled work, copy copies the full
  path.** A run page now opens with the Open filter; All and Needs attention stay one click away
  and `filter=all` survives every link. The run's sub line counts what it leaves out ("3 of 6
  done, 13 cancelled."), and the sidebar and overview counts use the same denominator. Cancelled
  items are hidden in the plan, an item's children and the backlog page behind a "Show N
  cancelled" link (`cancelled=show` in the URL). Every copy button copies the full path
  `project/run/ref`, from a new additive v1 field `fullRef`; a crumb line has one copy button, for
  the page's own path, and the item page shows that path above its title instead of a ref line
  below it. The copy icon is centred on the text beside it and no longer makes its line taller.
- **A worker can open a note that `work_search` finds.** A note hit routed to `work_history`, which a
  worker cannot call. For a worker the hit now prints the note in full under its row and routes to
  `work_get` on the item the note is about.
- **Every retry an ambiguous or unknown `scope` prints is a complete call.** `work_search` printed
  `work_search(scope:"run-a/1")` with no `q:`, so the retry was refused too. It now includes the
  query, and the limit if one was given. `work_brief` and the `work://item/<ref>/brief` resource keep
  `for:"worker"` (or `gate`) in their retries.
- **`work://item/<ref>/brief` lists an ambiguous ref's candidates in the same order as `work_get`.**
  It put the busiest run in the workspace first. Now both put the caller's own run first, and so does
  `work_brief(scope:)`.

- **`workstate-setup` now says when another registration hides the one it wrote.** A local-scope
  `workstate` entry for a repo (in `~/.claude.json`), or a project-scope one in its `.mcp.json`,
  wins over the user registration, so sessions there kept an old entry without the session helper
  while the script printed success. A new `shadows` line lists each such entry - directory, url,
  whether it has a helper, never a header or token - with the command that removes it
  (`claude mcp remove workstate -s local`, run in that repo). The script removes nothing itself.
  Checked by `find-shadows.mjs`, which needs `node`.
- **`work_brief(for:)` no longer falls back silently.** Any value but `resume`, `worker` or `gate`
  rendered a resume brief as if it were the one asked for; it is now refused, naming the legal values
  and what to use instead (`work_audit` for an audit, `work_history` for a retro). `for:"gate"` on
  anything but a phase is refused with the route to its phase.
- **The remaining bare refs.** `work_get`'s head row, the suggestions a missing scope prints, and
  the candidates `work://item/<ref>` lists for an ambiguous ref all call back as printed; the item
  resource lists the caller's own project first, as the tools do.
- **The `ADDRESSED TO YOU` header says who consumes.** It claimed every brief marks its notes
  consumed; only `for:"worker"` does. A resume brief now names the call that consumes them, and a
  worker brief says it did.
- **Minting a session right after a server restart no longer fails.** If the first request was
  `POST /api/agent-sessions`, the mint threw `InvalidCastException` reading its expiry, because the
  database type handlers were registered only once some other query had run. They are now
  registered when the server's core library loads.
- **WorkState server: the board, the tree and the attention block are faster on a large workspace.**
  Measured on a seeded 5,000-item workspace (docs/workstate/15-query-plans.md):
  - The dashboard board went from 77 ms to 31 ms, and the facts behind attention from 67 ms to 1 ms.
  - `work_tree`/`work_brief` subtrees went from 45 ms to 15 ms, and ref resolution from up to 14 ms to under 0.2 ms.
  - The fixes: a new index on `lower(ref)` (migration `RefLookupIndex`, applied on start); the rollup
    is counted per row instead of joining the `item_rollup` view; and the server's own database
    connections run with `jit=off`.
  - Results are unchanged.

## [2.4.0] - 2026-10-27

### Added

- **Session self-naming.** Hand-started sessions share one WorkState registration, so their work
  was signed by role alone. `workstate-setup` now registers a headers helper,
  `session-header.mjs` (needs `node`), that keys each session by its Claude Code process and sends
  its name when `claude agents` knows it. `work_session(name)` binds a hand-started session to the
  name `ListAgents` shows; `/rlarch:controller` and `/rlarch:orchestrate` call it when `work_brief`
  says the session is unnamed. A minted session keeps its name; two live sessions cannot share
  one. Existing installs: run the setup script once with `--session-names` (`-SessionNames` in
  PowerShell), then start a new session. A local-scope `workstate` entry in a repo overrides the
  user registration there and has no helper; `claude mcp remove workstate -s local` in that repo
  removes it.
- WorkState server **0.6.0** (rebuild to get it; one additive migration, `SessionClientKey`).

### Fixed

- **A bare `work_brief()` no longer hands out another project's run.** With several live projects
  and no repo known, it opened the busiest run of the whole workspace — often another project's.
  It now opens no run and lists the projects, newest activity first, each as a callable
  `work_brief(scope:"…")`, with `work_brief(repo:{remote, root})` as the first route. With one
  project, or none, the brief is unchanged. A minted session whose repo is bound lands its
  unscoped `work_next`, `work_tree`, `work_threads`, `work_search` and the `work://` resources in
  its own project; `work_get`'s attention block follows the run of the first ref it was given.
- **Refs printed by `work_create` and `work_search` call back as printed.** A ref another run also
  holds (`1`) is printed in its shortest unique form (`run-z/1`), in the rows and in the `Next:` line.

## [2.3.0] - 2026-10-26

### Added

- **`/rlarch:workstate-import`** — brings a repo's existing planning material into WorkState. The
  agent finds plans, TODO lists, roadmaps, specs, ADRs, docs and agent rules anywhere in the repo,
  checks each against the current code, keeps only what is still true (open work, binding rules and
  decisions, open assumptions — never history), proposes a per-file plan, waits for your yes, and
  writes it with `work_import`. Source files are never touched.
- **WorkState without a clone.** A public, multi-arch (amd64 + arm64) image,
  `ghcr.io/r-larch/workstate-api:<server version>` and `:latest`, published by
  `.github/workflows/workstate-image.yml` on every push to `master` that touches `mcp/`; and a
  pull-only compose shipped in `workstate-setup` (`assets/compose.yml`, same project name and volume
  as `mcp/compose.yml`). An install lives in `~/.workstate/`: that compose plus its `.env`.
- **`workstate-setup.sh` / `.ps1`** — one script for the whole setup: writes the `.env` with random
  tokens (never overwriting one), starts the stack, runs `claude mcp add` itself (`--role`,
  `--agent` for a named local-scope registration, `--force`, `--dry-run`), proves `tools/list` and
  prints only the tool count. `--backup <file>` / `--restore <file>` run `pg_dump -Fc` /
  `pg_restore --clean` in the `db` container; a restore writes a safety dump first. `--clone <path>`
  keeps a clone-based install on `<clone>/mcp/`.
- **`agent_launch` `workstate.grants`** — `["import"]` mints a session that can see `work_import`
  (role orchestrator or controller only; the server enforces it).
- WorkState server (rebuild with `docker compose -f mcp/compose.yml up -d --build` to get it; two
  additive migrations): the **`work_import`** tool — batches of runs, phases, tasks, followups,
  notes, assumptions and rules, each with its source and reason, idempotent by source, never `done`
  and never verified, listed only for an import-granted session. `work_get` and the dashboard show
  an imported item's source and reason.

### Changed

- **`/rlarch:workstate-setup` no longer handles tokens.** The model used to write the tokens into
  `mcp/.env`, paste one into `claude mcp add --header` and `curl` with it; now the script does all
  three and prints status lines with no secret in them, and the skill forbids reading the `.env` and
  running `claude mcp get` (which prints the token). No clone is needed any more. An existing
  clone-based install keeps working: run the script with `--clone <path>`.
- **One WorkState project per repository.** `orchestrate` and `controller` now start with
  `work_brief(repo: {remote, root})` — `remote` from `git remote get-url origin`, `root` the main
  checkout (the same from every worktree) — and create a project only when that brief says the repo
  is not bound to one. A second project for a bound repo is refused, naming the existing one.
- **`agent_launch` sends the repo's keys** when it mints a session, so a launched session's bare
  `work_brief()` opens its repo's project.
- WorkState server (rebuild to get it; migrations `ProjectRepos`, `ProjectArchive`, `ImportGrant`,
  `ImportRecord`, all additive). A session started before the rebuild sees the new tool parameters
  (`repo`, `archived`) only after a restart.
  - `work_brief(repo:…)` and `work_brief(scope:"<project>")` return a **project brief**: goal, every
    open run with its next action, the runs closed most recently with their commit, the backlog,
    open assumptions, binding rules and notes — and a NEXT that resumes a run or starts one.
  - **Text the reader needs is never cut**: shortening is allowed only in one-line list rows that
    carry a ref, and in echoes of the caller's own input; a test fails the build otherwise. The
    attention block keeps its 5-line cap and names `work://attention/<scope>`, which lists every line.
  - **Projects can be archived** (`work_update(ref, archived:true)` or the dashboard): hidden from the
    default lists, never deleted, still opened by ref and repo, un-archived by new work.
  - Dashboard: one project name everywhere (name, with the ref beside it when they differ); projects
    by last activity with search and Active / Quiet / Archived groups; the sidebar lists only open
    runs; a project page; **prompt actions** — buttons that copy a ready prompt for a fresh session
    (Continue, Status report, Housekeeping, Triage backlog, Finalize, Resume, Audit, Work on this,
    Investigate block), served by `GET /api/v1/prompts`.
- `nested-claude` §9: audit every imperative in a brief, not only its prohibitions, and write a
  continuation brief that builds on the work already on disk.

### Fixed

- WorkState server: `work_tree(status:"in_progress")` matched nothing; relative times read "now
  ago"; `work_get` could not open an assumption (`A1`); `work_threads` counted wrapped lines instead
  of rows; `work_import` dropped a `ref` on note and assumption rows silently.

## [2.2.1] - 2026-10-26

### Fixed

- **dotnet-source on macOS/Linux: the daemon survives its caller.** It now starts its own session and
  ignores SIGHUP, so it no longer dies when the shell or harness that ran `ds serve` kills its process
  group or hangs up. (Windows unchanged.)
- **dotnet-source on macOS/Linux: a killed daemon no longer blocks every later `serve`.** Its marker
  outlived it (`DeleteOnClose` is not kernel-backed on Unix) and each new `serve` exited 2 until the
  file was deleted by hand. The single-instance lock is now a kernel `flock`, which is released when
  the process dies. Verified on Linux (WSL); macOS uses the same calls but is not tested.
- WorkState server (rebuild with `docker compose -f mcp/compose.yml up -d --build` to get it):
  `work_get` prints intent, criteria and evidence in full; no `Next:` trailer proposes moving an item
  that is already closed; `done` is accepted from `ready` for an item that was started and whose
  lease lapsed.

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
- **The `agents` MCP server** (plugin stdio server, Node 18+, no dependencies): `agent_launch`,
  `agent_stop`, `agent_list`. The caller sets the permission mode, toolset, extra tools and dirs,
  model, effort, settings and system-prompt suffix per launch; the argv is built in code with every
  variadic flag closed and the prompt last. The scripts remain as the fallback.
  See [docs/workstate/14-agent-launch.md](docs/workstate/14-agent-launch.md).
- **Per-session WorkState tokens** (`/api/agent-sessions`): `agent_launch` mints each agent its own
  token and role, and `agent_stop` revokes it — no token ever appears in a tool result or a brief.
  A minted token's `ttlHours` (default 72) is an idle window that every use renews, with no hard cap:
  it lapses only after that long unused.

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
