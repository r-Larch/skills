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
| `pnpm --dir mcp/frontend verify:journal` | the cursor key and where the "since you last looked" divider lands — see below |
| `pnpm --dir mcp/frontend verify:empty` | the three panel states: unknown, empty, populated — see below |

Both verify scripts need the API running. They read the dashboard credential from
`WorkState.Api/appsettings.Development.json`, or from `WORKSTATE_USER` / `WORKSTATE_PASSWORD`. No
credential is written into any file here.

### Why `verify` exists

Rule 1 of this unit: the centre pane is the agent's artifact, byte for byte. `src/brief.ts` is the
only transform it applies, and it is deliberately shaped so that `rejoin(briefLines(x)) === x` is
true for every input — the script asserts exactly that, against six adversarial strings and against
the live endpoint. If a future change makes the pane do something a `<pre>` would not, this fails.

### Why `verify:journal` exists

The first version of the journal pane could never render the divider. It recomputed the
`localStorage` key from `props.run` inside `onCleanup` — which runs *after* the route has changed —
so it wrote `workstate.seen.undefined` while `onMount` had read `workstate.seen.<run>`. The read key
and the write key never matched. The script reproduces that on the real solid-js reactive core and
proves the fix, then checks where the divider lands against the live journal.

The two things that keep it fixed: `RunBody` is **keyed** on the run, so every run gets its own
component instance and `run` is a plain string rather than a live param read; and the key is computed
once, at creation, and used for both the read and the write.

### Why `verify:empty` exists

Every panel on the watch floor used `value?.length ?? 0`, which collapses *not asked yet* into
*asked, and the answer is none*. A first paint rendered "Honesty across 8 runs" beside "Runs 0 / No
run has been imported yet." — and worse, "Nothing needs you." before the request had returned. Doc 09
§1 says this view exists to answer *"does anything need me right now?"*; a supervision console may
say no only when it knows.

So presence is three states, not two, and `src/fill.ts` owns the decision so the collapse cannot be
written again by hand. **Unknown renders `—`** — already this product's mark for "no value here"
(doc 06 §3), and static, because doc 09 §6 spends motion on the lease clock alone. The script checks
the state sequence over a deliberately late answer on the real reactive core (`"—" → "106 row(s)"`,
never the empty sentence), and greps `WatchFloor.tsx` for the shape that caused it.

## Layout

```
src/
  api.ts      the App API, typed — one 401 flips the whole app to the sign-in gate
  brief.ts    the centre pane's only transform (lines in, lines out, text untouched)
  clock.ts    lease arithmetic in the *server's* frame of reference, via /api/watch's asOf
  fill.ts     unknown vs empty vs populated, and the counts that must not read 0 too early
  journal.ts  the cursor key, and where the "since you last looked" divider goes
  live.ts     one EventSource on /api/stream; a frame means "re-read", never "here is the state"
  nav.ts      where a qualifiedRef points
  queue.ts    the attention queue's grouping and the honesty strip's four cells
  theme.ts    dark · light · system on [data-theme]
  views/      WatchFloor.tsx, RunView.tsx — the two views of doc 09 §5, and no third
verify/       the four checks above
```
