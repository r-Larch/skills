# 13 — The dashboard

Replaces [09](09-dashboard-design.md). Built from the prototype the owner approved (WorkState item
`mature/ui`), on the [v1 read API](10-read-api.md) and nothing else. Read-only: the MCP tools stay
the only write path. Code: `mcp/frontend` (SolidJS, `@solidjs/router`, Vite, Tailwind 4; no other
dependency).

The dashboard does not call the pre-v1 routes (`/api/projects`, `/api/watch`,
`/api/runs/{ref}/tree|brief|journal`), and they are gone from the server (unit
`mature/prev1-removal`); the tests that reached present behaviour through them now go through v1.

## Information architecture

A sidebar (Overview, Needs you with its count, Activity; then every project and its runs, each
with a done/total count and a thin progress bar) and one page beside it. Every page is a URL.

| URL | Page | Reads |
|---|---|---|
| `/` | **Overview** — Needs you (top 5), Working now, four honesty counts, then one panel per project: a serif header band, open runs first, closed runs dimmed below, each run a segmented bar with **one segment per leaf**, coloured by state; the legend once below | `overview`, `inbox`, every run's `board` |
| `/inbox` | **Needs you** — everything waiting on a person, newest first, each row with the MCP call that resolves it | `inbox` |
| `/activity` | **Activity** — the last 300 journal events across the workspace, grouped by day | `journal?limit=300` |
| `/runs/{run}` | **Run** — plan list (filters All / Open / Needs attention; phases as group headers; full titles; ref; claim holder; criteria x/y) beside the detail panel for the run | `runs/{run}/board`, `items/{run}` |
| `/runs/{run}/{item}` | the same, with `{item}` selected | `runs/{run}/board`, `items/{item}` |
| `/items/{id}` | resolves any item: if it sits in a run it becomes `/runs/{run}/{id}`; otherwise (a project, project-level work) it is shown standalone | `items/{id}` |
| `/projects/{id}/backlog` | **Backlog** — the project's open deferrals, grouped by the run they came from | `backlog?project={id}` |

`?filter=open|attention` and `?tab=evidence|notes|activity|brief` ride on the run URL, so a
selection is deep-linkable and back/forward walk it.

The **detail panel**: crumbs, full title and qualified ref, a state pill, chips (kind, commit,
claim holder, tree, priority, severity, resolution, closed/started), then tabs:

- **Overview** — what it is for, what it waits on, its done-when criteria with evidence, the verify
  command, findings against it, what is inside it.
- **Evidence** — two columns: *What the agent reported* (`work_report`, a claim) and *What was
  verified* (`work_verify`, run by someone other than the author).
- **Notes**, **Activity** — the item's own.
- **Brief** — the verbatim `work_brief`, as text. Never the main pane.

A claim holder is shown by session name when the claim has one, else by the actor label.

## The state vocabulary

One function, `src/state.ts` `state()`, colours every dot, pill and segment. First match wins:

| state | when | drawn |
|---|---|---|
| `gone` | superseded, cancelled, or done with resolution `obsolete` (a deferral: *Moved*) | grey; left out of bars |
| `stop` | blocked; or a claim expired on an item not closed | red, ringed |
| `doing` | in progress or in review | blue, breathing (static under reduced motion) |
| `proven` | done, and either a container or a leaf whose latest verification passed and was not run by a worker | green |
| `claimed` | done leaf without that | amber — *Done, not verified* |
| `wait` | open/ready with an open `blocks` blocker | dashed ring; **not** an inbox row |
| `idle` | open | hollow ring |

Leaves are the doc 10 rollup kinds: task, followup, subtask, spike, question, incident.
`verify/state-cases.mjs` enumerates every case and fails on any change of precedence.

## Theme tokens

`data-theme` on `<html>` (`light` / `dark`), unset = follow `prefers-color-scheme`. The toggle in
the sidebar footer stores the choice in `localStorage` (`workstate.theme`), guarded for storage
that throws.

| token | light (DIKAIA) | dark (VS Code Dark Modern, softened) |
|---|---|---|
| `--side` / `--side-2` | `#1d1b19` / `#2a2724` | `#181818` / `#2a2d2e` |
| `--side-ink` / `--side-mute` | `#e9e4da` / `#9c958a` | `#c8c8c8` / `#808080` |
| `--gold` (active marker) | `#d1a85a` | `#3794ff` |
| `--bg` / `--panel` | `#f6f4ef` / `#ffffff` | `#1f1f1f` / `#181818` |
| `--line` / `--line-2` | `#e8e4dc` / `#f0ede7` | `#2b2b2b` / `#262626` |
| `--ink` / `--ink-2` / `--ink-3` | `#1f1d1a` / `#5d5850` / `#948e84` | `#c8c8c8` / `#a6a6a6` / `#808080` |
| `--proj-bg` | `linear-gradient(16deg, #f3f0e9, #f8f6f1)` | `linear-gradient(16deg, #121212, #151515)` |
| `--accent` | `#4f5bd5` | `#3794ff` |
| `--proven` | `#1f8a5b` | `#4ec9b0` |
| `--claimed` | `#b7791f` | `#dcdcaa` |
| `--doing` | `#4f5bd5` | `#3794ff` |
| `--stop` | `#c2362b` | `#f14c4c` |
| `--idle` / `--gone` | `#cfc9be` / `#bdb7ad` | `#474747` / `#555555` |

Fonts: Inter (UI), JetBrains Mono (refs, commits, commands), Libre Baskerville (page titles, brand,
project headers), from Google Fonts. Icons are inline SVG.

## Live updates

One `EventSource` on `/api/stream`. Frames are debounced (400 ms) into a burst; a burst re-reads
the overview, the inbox, the boards of the runs the frames name (all boards on a reconnect or a
frame with no run), and the current page's own item or journal read. A frame is never applied as
state.

## Checks

`pnpm --dir mcp/frontend verify` (no server) · `verify:board` and `verify:links` (a running API and
a dashboard credential) — see `mcp/frontend/README.md`.
