import { For, Show, createMemo, createResource, createSignal } from 'solid-js'
import { A } from '@solidjs/router'
import * as api from '../api'
import type { Honesty } from '../api'
import { clockFace, remainingMs, skewFrom, staticFace, reducedMotion, ticker } from '../clock'
import { pulse } from '../live'
import { refHref } from '../nav'
import { PREVIEW, TONE, group, honestyCells } from '../queue'
import type { Group } from '../queue'
import TopBar from '../TopBar'

export default function WatchFloor() {
  const [scope, setScope] = createSignal<string | undefined>(undefined)

  // Solid re-throws a rejected resource when its accessor is read, which would take the whole board
  // down over one failed request. A read that fails here should leave the board saying so, not blank.
  const [failed, setFailed] = createSignal('')
  const guard = async <T,>(work: () => Promise<T>): Promise<T | undefined> => {
    try {
      const value = await work()
      setFailed('')
      return value
    } catch (error) {
      if (!(error instanceof api.Unauthorized)) setFailed(String(error))
      return undefined
    }
  }

  const [projects] = createResource(() => guard(api.projects))

  // `pulse()` in the source is the whole live story on this view: an SSE frame carries a seq and a
  // kind, so the honest response is to re-read the board it invalidated. Nothing here is on a timer.
  const [board] = createResource(
    () => ({ project: scope(), at: pulse() }),
    (key: { project: string | undefined }) => guard(() => api.watch(key.project)),
  )

  // No endpoint lists runs (see the report): the project subtree is the only place they exist, and
  // filtering it to kind === "run" also carries each run's rollup, which a bare list would not.
  const [runs] = createResource(
    () => ({ projects: projects(), at: pulse() }),
    (key: { projects: api.Project[] | undefined }) =>
      guard(async () => {
        const refs = (key.projects ?? []).map((p) => p.ref)
        const trees = await Promise.all(refs.map((ref) => api.tree(ref)))
        return trees.flatMap((t) => t.nodes.filter((n) => n.kind === 'run' && n.depth === 1))
      }),
  )

  const now = ticker()
  const skew = createMemo(() => skewFrom(board()?.asOf ?? new Date().toISOString()))
  const runCount = createMemo(() => (projects() ?? []).reduce((sum, p) => sum + p.runCount, 0))

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
            {board()?.workingNow.length ?? 0}
            {(board()?.workingNow.length ?? 0) === 1 ? ' claim' : ' claims'}
            {scope() === undefined ? '' : ` · ${scope()}`}
          </span>
        </div>

        <div class="panel claims">
          <Show
            when={(board()?.workingNow.length ?? 0) > 0}
            fallback={
              <p class="empty">
                No claim is held right now. Nothing is being worked, or nothing has said so.
              </p>
            }
          >
            <For each={board()?.workingNow}>
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
          </Show>
        </div>

        <Show when={(board()?.workingNow ?? []).some((l) => remainingMs(l.expiresAt, now(), skew()) < 0)}>
          <p class="note">
            A lease that runs past zero is the only way a stopped agent announces itself. It stopped
            writing; the store noticed.
          </p>
        </Show>

        <div class="sec-row">
          <h2 class="sec">Needs you</h2>
          <span class="count">{board()?.needsYou.length ?? 0} open</span>
        </div>

        <Show
          when={(board()?.needsYou.length ?? 0) > 0}
          fallback={
            <div class="panel">
              <p class="empty">Nothing needs you.</p>
            </div>
          }
        >
          <For each={group(board()?.needsYou ?? [])}>{(g) => <RankGroup group={g} />}</For>
        </Show>

        <div class="sec-row">
          <h2 class="sec">Honesty</h2>
          <span class="count">
            across {runCount()} runs
            {scope() === undefined ? '' : ` · ${scope()}`}
          </span>
        </div>
        <HonestyStrip honesty={board()?.honesty} />

        <div class="sec-row">
          <h2 class="sec">Runs</h2>
          <span class="count">{runs()?.length ?? 0}</span>
        </div>
        <div class="panel runs">
          <Show
            when={(runs()?.length ?? 0) > 0}
            fallback={<p class="empty">No run has been imported yet.</p>}
          >
            <For each={runs()}>
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
          </Show>
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
              <Show when={row.qualifiedRef} fallback={<>{row.ref ?? '—'}</>}>
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

/**
 * doc 09 §5. Four cells, and the fourth is deliberately unlike the other three: an open follow-up is
 * doc 07 §4 working, not an accusation, so it carries no hue. Nor does a zero — a count that needs no
 * decision gets no colour, which is doc 06 rule 4 applied to the strip that exists to break it.
 */
function HonestyStrip(props: { honesty: Honesty | undefined }) {
  const cells = () =>
    honestyCells(
      props.honesty ?? { doneUnverified: 0, forced: 0, ungated: 0, followupsOpen: 0 },
    )

  return (
    <div class="honesty">
      <For each={cells()}>
        {(cell) => (
          <div class="hcell">
            <div class={`hnum tnum ${cell.tone}`}>{cell.n}</div>
            <div class="hlab">{cell.label}</div>
            <div class="hnote">{cell.note}</div>
          </div>
        )}
      </For>
    </div>
  )
}
