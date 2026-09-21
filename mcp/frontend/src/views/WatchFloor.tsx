import { For, Match, Show, Switch, createEffect, createMemo, createSignal, on } from 'solid-js'
import type { JSX } from 'solid-js'
import { A } from '@solidjs/router'
import * as api from '../api'
import type { Project, TreeNode, Watch } from '../api'
import { clockFace, remainingMs, skewFrom, staticFace, reducedMotion, ticker } from '../clock'
import { UNKNOWN, count, countOf, fill, plural } from '../fill'
import { pulse } from '../live'
import { refHref } from '../nav'
import { PREVIEW, TONE, group, honestyCells } from '../queue'
import type { Group } from '../queue'
import TopBar from '../TopBar'

export default function WatchFloor() {
  const [scope, setScope] = createSignal<string | undefined>(undefined)
  const [failed, setFailed] = createSignal('')

  // `undefined` is "no answer has ever arrived", and it is the initial state of all three. A failed
  // read leaves whatever was last known in place rather than inventing an emptiness — which is why
  // these are plain signals and not resources: a resource re-enters pending on every source change,
  // so a pulse would blank the board to "unknown" once a second on a busy day.
  const [projects, setProjects] = createSignal<Project[] | undefined>(undefined)
  const [board, setBoard] = createSignal<Watch | undefined>(undefined)
  const [runs, setRuns] = createSignal<TreeNode[] | undefined>(undefined)

  const load = async <T,>(work: () => Promise<T>, keep: (value: T) => void): Promise<void> => {
    try {
      keep(await work())
      setFailed('')
    } catch (error) {
      if (!(error instanceof api.Unauthorized)) setFailed(String(error))
    }
  }

  // A pulse carries only a seq and a kind, so the honest response is to re-read what it invalidated.
  // Nothing here is on a timer.
  createEffect(on(pulse, () => void load(api.projects, (value) => setProjects(() => value))))

  createEffect(
    on([scope, pulse], ([project]) =>
      void load(() => api.watch(project), (value) => setBoard(() => value)),
    ),
  )

  // No endpoint lists runs (reported): the project subtree is the only place they exist, and
  // filtering it to kind === "run" also carries each run's rollup, which a bare list would not.
  createEffect(
    on([projects, pulse], ([list]) => {
      if (list === undefined) return

      void load(
        async () => {
          const trees = await Promise.all(list.map((p) => api.tree(p.ref)))
          return trees.flatMap((t) => t.nodes.filter((n) => n.kind === 'run' && n.depth === 1))
        },
        (value) => setRuns(() => value),
      )
    }),
  )

  const now = ticker()
  const skew = createMemo(() => skewFrom(board()?.asOf ?? new Date().toISOString()))
  const runCount = createMemo(() =>
    projects() === undefined
      ? UNKNOWN
      : String((projects() ?? []).reduce((sum, p) => sum + p.runCount, 0)),
  )
  const lapsedLease = createMemo(() =>
    (board()?.workingNow ?? []).some((l) => remainingMs(l.expiresAt, now(), skew()) < 0),
  )

  return (
    <>
      <TopBar
        subtitle="watch floor"
        nav={
          <nav class="projects" aria-label="Project">
            <button aria-current={scope() === undefined} onClick={() => setScope(undefined)}>
              all
            </button>
            <For each={projects() ?? []}>
              {(project) => (
                <button
                  aria-current={scope() === project.ref}
                  onClick={() => setScope(project.ref)}
                >
                  {project.ref}
                </button>
              )}
            </For>
          </nav>
        }
      />

      <div class="wrap">
        <div class="sec-row">
          <h2 class="sec">Working now</h2>
          <span class="count">
            {countOf(board()?.workingNow, plural(board()?.workingNow, 'claim'))}
            {scope() === undefined ? '' : ` · ${scope()}`}
          </span>
        </div>

        <div class="panel claims">
          <Filled
            of={board()?.workingNow}
            empty="No claim is held right now. Nothing is being worked, or nothing has said so."
          >
            {(leases) => (
              <For each={leases}>
                {(lease) => {
                  const left = () => remainingMs(lease.expiresAt, now(), skew())
                  const lapsed = () => left() < 0
                  return (
                    <div class="row">
                      <span class={`dot ${lapsed() ? 'stale' : 'on'}`} />
                      <span class="who">{lease.agent}</span>
                      <span class="what" title={`${lease.qualifiedRef} ${lease.title}`}>
                        <A href={refHref(lease.qualifiedRef)} class="ref">
                          {lease.qualifiedRef}
                        </A>{' '}
                        {lease.title}
                      </span>
                      <span
                        class={`clock tnum ${lapsed() ? 'past' : ''}`}
                        title={`lease expires ${lease.expiresAt}`}
                      >
                        {reducedMotion() ? staticFace(left()) : clockFace(left())}
                      </span>
                    </div>
                  )
                }}
              </For>
            )}
          </Filled>
        </div>

        <Show when={lapsedLease()}>
          <p class="note">
            A lease that runs past zero is the only way a stopped agent announces itself. It stopped
            writing; the store noticed.
          </p>
        </Show>

        <div class="sec-row">
          <h2 class="sec">Needs you</h2>
          <span class="count">{countOf(board()?.needsYou, 'open')}</span>
        </div>

        <Switch>
          <Match when={fill(board()?.needsYou) === 'unknown'}>
            <div class="panel">
              <Unknown />
            </div>
          </Match>
          <Match when={fill(board()?.needsYou) === 'empty'}>
            <div class="panel">
              <p class="empty">Nothing needs you.</p>
            </div>
          </Match>
          <Match when={fill(board()?.needsYou) === 'rows'}>
            <For each={group(board()?.needsYou ?? [])}>{(g) => <RankGroup group={g} />}</For>
          </Match>
        </Switch>

        <div class="sec-row">
          <h2 class="sec">Honesty</h2>
          <span class="count">
            across {runCount()} runs
            {scope() === undefined ? '' : ` · ${scope()}`}
          </span>
        </div>
        <div class="honesty">
          <For each={honestyCells(board()?.honesty)}>
            {(cell) => (
              <div class="hcell">
                <div class={`hnum tnum ${cell.tone}`}>{cell.n}</div>
                <div class="hlab">{cell.label}</div>
                <div class="hnote">{cell.note}</div>
              </div>
            )}
          </For>
        </div>

        <div class="sec-row">
          <h2 class="sec">Runs</h2>
          <span class="count">{count(runs())}</span>
        </div>
        <div class="panel runs">
          <Filled of={runs()} empty="No run has been imported yet.">
            {(rows) => (
              <For each={rows}>
                {(run) => (
                  <A href={refHref(run.ref)} class="row">
                    <span class="ref" title={run.title}>
                      {run.ref} <span class="what">{run.title}</span>
                    </span>
                    <span class="count tnum">
                      {run.doneCount}/{run.totalCount}
                      <Show when={run.disagrees}>
                        {' '}
                        <span
                          class="warnmark"
                          title={`${run.status} with ${run.totalCount - run.doneCount} of ${run.totalCount} children not done`}
                        >
                          ⚠
                        </span>
                      </Show>
                    </span>
                    <span class={`st ${run.status}`}>{run.status}</span>
                  </A>
                )}
              </For>
            )}
          </Filled>
        </div>

        <div class="foot">
          <Show when={failed() !== ''}>
            <span style={{ color: 'var(--color-attention)' }}>{failed()}</span>
          </Show>
          <span>
            Colour appears only where something needs a decision. A run with nothing wrong is
            entirely grey.
          </span>
        </div>
      </div>
    </>
  )
}

/** What a panel shows before its first answer. Static, because doc 09 §6 spends motion on the clock. */
const Unknown = () => <p class="empty unknown">{UNKNOWN}</p>

/**
 * The only place a list decides what to draw, so `value?.length ?? 0` cannot reappear at a call site.
 * `unknown` and `empty` are different facts and they now have different renderings.
 */
function Filled<T>(props: {
  of: readonly T[] | undefined
  empty: string
  children: (rows: readonly T[]) => JSX.Element
}) {
  return (
    <Switch>
      <Match when={fill(props.of) === 'unknown'}>
        <Unknown />
      </Match>
      <Match when={fill(props.of) === 'empty'}>
        <p class="empty">{props.empty}</p>
      </Match>
      <Match when={fill(props.of) === 'rows'}>{props.children(props.of ?? [])}</Match>
    </Switch>
  )
}

function RankGroup(props: { group: Group }) {
  const [all, setAll] = createSignal(false)
  const rows = () => (all() ? props.group.rows : props.group.rows.slice(0, PREVIEW))
  const hidden = () => props.group.rows.length - rows().length

  return (
    <details class="group attn" open>
      <summary>
        <span class={`rank ${TONE[props.group.rank] ?? ''}`}>{props.group.rank}</span>
        <span class="count">
          {props.group.rows.length}
          {props.group.runs > 1 ? ` · across ${props.group.runs} runs` : ''}
        </span>
      </summary>

      <For each={rows()}>
        {(row) => (
          <div class="row">
            <span class="ref" title={row.qualifiedRef ?? row.ref ?? ''}>
              <Show when={row.qualifiedRef} fallback={<>{row.ref ?? UNKNOWN}</>}>
                {(qualified) => <A href={refHref(qualified())}>{qualified()}</A>}
              </Show>
            </span>
            <span class="msg" title={row.what}>
              {row.what}
            </span>
            <span class="call" title={row.call}>
              {row.call}
            </span>
          </div>
        )}
      </For>

      <Show when={hidden() > 0}>
        <button class="more" onClick={() => setAll(true)}>
          + {hidden()} more, ranked below these
        </button>
      </Show>
    </details>
  )
}
