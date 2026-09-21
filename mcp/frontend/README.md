# `mcp/frontend` — the watch floor

The operator dashboard of `docs/workstate/09-dashboard-design.md`. SolidJS + Vite + Tailwind 4, two
views, read-only. It talks to the App API over the dashboard cookie and never to `/mcp`.

## The one command that builds and serves it

```
dotnet build mcp/WorkState.Api/WorkState.Api.csproj -p:BuildSpa=true
dotnet run   --project mcp/WorkState.Api/WorkState.Api.csproj
```

The first runs `pnpm install --frozen-lockfile` and `pnpm run build`, and Vite writes straight into
`WorkState.Api/wwwroot` — its `outDir`, so there is nothing to copy. The second serves it at
<http://localhost:5290>. **`-p:BuildSpa=true` is opt-in**: a plain `dotnet build` never needs Node.

`wwwroot/` is gitignored build output. Nothing under it is committed.

## Developing against a running API

```
dotnet run --project mcp/WorkState.Api/WorkState.Api.csproj    # port 5290
pnpm --dir mcp/frontend dev                                    # port 5173, proxies /api to 5290
```

The dev server proxies `/api` so the session cookie stays same-origin — dev and the built app have
one auth story, not two.

## Checks

| | |
|---|---|
| `pnpm --dir mcp/frontend ts` | typecheck, no emit |
| `pnpm --dir mcp/frontend build` | production build into `wwwroot` |
| `pnpm --dir mcp/frontend verify` | **the brief is byte-identical** — see below |
| `pnpm --dir mcp/frontend verify:board` | prints the honesty counts, the queue grouping, the lease clock and the ⚠ rows, computed by the same modules the views render with |

Both verify scripts need the API running. They read the dashboard credential from
`WorkState.Api/appsettings.Development.json`, or from `WORKSTATE_USER` / `WORKSTATE_PASSWORD`. No
credential is written into any file here.

### Why `verify` exists

Rule 1 of this unit: the centre pane is the agent's artifact, byte for byte. `src/brief.ts` is the
only transform it applies, and it is deliberately shaped so that `rejoin(briefLines(x)) === x` is
true for every input — the script asserts exactly that, against six adversarial strings and against
the live endpoint. If a future change makes the pane do something a `<pre>` would not, this fails.

## Layout

```
src/
  api.ts      the App API, typed — one 401 flips the whole app to the sign-in gate
  brief.ts    the centre pane's only transform (lines in, lines out, text untouched)
  clock.ts    lease arithmetic in the *server's* frame of reference, via /api/watch's asOf
  live.ts     one EventSource on /api/stream; a frame means "re-read", never "here is the state"
  nav.ts      where a qualifiedRef points
  queue.ts    the attention queue's grouping and the honesty strip's four cells
  theme.ts    dark · light · system on [data-theme]
  views/      WatchFloor.tsx, RunView.tsx — the two views of doc 09 §5, and no third
verify/       the two checks above
```
