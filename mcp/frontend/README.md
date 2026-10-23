# `mcp/frontend` — the dashboard

The operator dashboard of `docs/workstate/13-dashboard.md`: Overview, Needs you, Activity, a page
per run (plan list + detail panel), a backlog per project. SolidJS + Vite + Tailwind 4, read-only.
It reads only the v1 JSON API (`docs/workstate/10-read-api.md`) and `/api/stream`, over the
dashboard cookie, and never `/mcp`.

## The one command that builds and serves it

```
dotnet build mcp/WorkState.Api/WorkState.Api.csproj -p:BuildSpa=true
dotnet run   --project mcp/WorkState.Api/WorkState.Api.csproj
```

The first runs `pnpm install --frozen-lockfile` and `pnpm run build`, and Vite writes straight into
`WorkState.Api/wwwroot` — its `outDir`, so there is nothing to copy. The second serves it at
<http://127.0.0.1:5290>. **`-p:BuildSpa=true` is opt-in**: a plain `dotnet build` never needs Node.

`wwwroot/` is gitignored build output. Nothing under it is committed.

## Developing against a running API

```
dotnet run --project mcp/WorkState.Api/WorkState.Api.csproj    # port 5290, IPv4 loopback only
pnpm --dir mcp/frontend dev                                    # port 5173, proxies /api to 127.0.0.1:5290
```

The dev server proxies `/api` so the session cookie stays same-origin — dev and the built app have
one auth story, not two.

## Checks

| | |
|---|---|
| `pnpm --dir mcp/frontend ts` | typecheck, no emit |
| `pnpm --dir mcp/frontend build` | production build into `wwwroot` |
| `pnpm --dir mcp/frontend verify` | **no server.** Every case of the state vocabulary in precedence order, the segment tally, the plan filters, and the link builders |
| `pnpm --dir mcp/frontend verify:board [base] [runRef]` | live: per-run state counts as the bars draw them, one run leaf by leaf (default `mature`), the honesty strip, the inbox summary, working now |
| `pnpm --dir mcp/frontend verify:links [base]` | live: every href the UI can draw, checked against the API reads its page makes — exit 1 on any dead link |

The live scripts sign in with the dashboard credential from `WORKSTATE_USER` /
`WORKSTATE_PASSWORD`, else `mcp/.env` (`WORKSTATE_DASHBOARD_USERNAME` / `_PASSWORD`, what
`compose.yml` passes the container), else `WorkState.Api/appsettings.Development.json`. No
credential is written into any file here. Base defaults to `http://127.0.0.1:5290`.

## Layout

```
src/
  api.ts      the v1 API, typed — one 401 flips the whole app to the sign-in gate
  state.ts    the state vocabulary: state(), and tally() for the segmented bars
  plan.ts     the run page's plan list: grouping by phase and the three filters
  inbox.ts    inbox kinds in words, the inbox summary, the honesty counts
  nav.ts      every href to an item, and the reads each route makes (for verify:links)
  store.ts    overview + inbox + every run's board, re-read per burst of stream frames
  live.ts     one EventSource on /api/stream; frames debounced into bursts
  theme.ts    light / dark on [data-theme], default follows the system
  format.ts   relative times and title trimming
  Shell.tsx   sidebar + main;  ui.tsx  dots, pills, segments, legend;  icons.tsx  inline SVG
  views/      Overview, Inbox, Activity, Run (+ Detail), Item, Backlog
verify/       the three checks above, and session.mjs (sign-in)
```
