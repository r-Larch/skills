import { For, Index, Show } from 'solid-js'
import { A } from '@solidjs/router'
import type { Claim, ProjectOverview, RunOverview } from '../api.ts'
import { boards, inbox, overview } from '../store.ts'
import { CLOSED, refState, tally } from '../state.ts'
import { backlogHref, itemHref, runHref } from '../nav.ts'
import { honestyCells, openTasks } from '../inbox.ts'
import { UNKNOWN, ago, clock, day, plural, short } from '../format.ts'
import { Dot, Known, Legend, Segs } from '../ui.tsx'
import { InboxRowView } from './Inbox.tsx'

export default function Overview() {
  const sub = () => {
    const o = overview()
    if (o === undefined) return 'Loading…'
    const open = openTasks(o.projects)
    const n = o.inboxCount
    const projects = `${o.projects.length} ${plural(o.projects.length, 'project')}`
    return `${open} ${plural(open, 'task')} open across ${projects}. ${
      n > 0 ? `${n} ${plural(n, 'thing')} ${n === 1 ? 'needs' : 'need'} you.` : 'Nothing needs you.'
    }`
  }

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Overview</h1>
          <div class="sub">{sub()}</div>
        </div>
        <Show when={overview()}>{(o) => <div class="stamp">As of {day(o().asOf)}, {clock(o().asOf)}</div>}</Show>
      </div>

      <div class="stack">
        <div class="grid-2">
          <div class="panel">
            <div class="panel-h">
              <h2>Needs you</h2>
              <span class="n">{inbox()?.rows.length ?? UNKNOWN}</span>
              <A class="r" href="/inbox">
                Open inbox
              </A>
            </div>
            <Known value={inbox()}>
              {(box) => (
                <Index each={box().rows.slice(0, 5)} fallback={<div class="empty">Nothing is waiting on you.</div>}>
                  {(row) => <InboxRowView row={row()} asOf={box().asOf} />}
                </Index>
              )}
            </Known>
          </div>

          <div class="stack">
            <div class="panel">
              <div class="panel-h">
                <h2>Working now</h2>
                <span class="n">{overview()?.workingNow.length ?? UNKNOWN}</span>
              </div>
              <Known value={overview()}>
                {(o) => (
                  <Index each={o().workingNow} fallback={<div class="empty">No agent holds a claim.</div>}>
                    {(claim) => <ClaimRow claim={claim()} asOf={o().asOf} />}
                  </Index>
                )}
              </Known>
            </div>

            <div class="panel">
              <div class="counts">
                <Show
                  when={overview()}
                  fallback={
                    <For each={['closed without independent proof', 'forced closures', 'follow-ups open', 'tasks open']}>
                      {(label) => (
                        <div class="count">
                          <strong>{UNKNOWN}</strong>
                          <span>{label}</span>
                        </div>
                      )}
                    </For>
                  }
                >
                  {(o) => (
                    <For each={honestyCells(o().honesty, o().projects)}>
                      {(cell) => (
                        <div class="count">
                          <strong class={cell.bad ? 'bad' : ''}>{cell.n}</strong>
                          <span>{cell.label}</span>
                        </div>
                      )}
                    </For>
                  )}
                </Show>
              </div>
            </div>
          </div>
        </div>

        <Known value={overview()} class="panel empty">
          {(o) => (
            <Index each={o().projects} fallback={<div class="panel empty">No project has been created yet.</div>}>
              {(project) => <ProjectPanel project={project()} />}
            </Index>
          )}
        </Known>

        <Legend />
      </div>
    </div>
  )
}

function ClaimRow(props: { claim: Claim; asOf: string }) {
  const c = () => props.claim
  const holder = () => c().session?.name ?? c().agent
  const lease = () => (c().expired ? `lease expired ${ago(c().expiresAt, props.asOf)}` : `lease ends ${ago(c().expiresAt, props.asOf)}`)

  return (
    <A class="row" href={itemHref(c().item, c().run)}>
      <Dot k={c().expired ? 'stop' : 'doing'} />
      <div>
        <div class="row-t">{c().item.title}</div>
        <div class="row-m">
          <span class="who">{holder()}</span> · {c().item.qualifiedRef} · {c().tree ? `${c().tree} · ` : ''}
          {lease()}
        </div>
      </div>
      <span />
    </A>
  )
}

function ProjectPanel(props: { project: ProjectOverview }) {
  const name = () => props.project.item.title.split(' — ')
  const runs = () => {
    const all = props.project.runs
    return [...all.filter((r) => !CLOSED.has(r.item.status)), ...all.filter((r) => CLOSED.has(r.item.status))]
  }
  const live = () => props.project.runs.filter((r) => !CLOSED.has(r.item.status)).length

  return (
    <div class="panel runs">
      <div class="proj-h">
        <div>
          <h2>{name()[0]}</h2>
          <Show when={name().length > 1}>
            <div class="strip-m">{name().slice(1).join(' — ')}</div>
          </Show>
        </div>
        <span class="n">
          {live()} open, {props.project.runs.length - live()} closed
        </span>
        <A class="backlink" href={backlogHref(props.project.item.id)}>
          Backlog
        </A>
      </div>
      <Index each={runs()} fallback={<div class="empty">No runs yet.</div>}>
        {(run) => <RunStrip run={run()} />}
      </Index>
    </div>
  )
}

function RunStrip(props: { run: RunOverview }) {
  const board = () => boards()[props.run.item.id]
  const t = () => {
    const b = board()
    return b ? tally(b.rows) : undefined
  }
  const closed = () => CLOSED.has(props.run.item.status)

  const meta = () => {
    const b = board()
    const holding = b?.rows.find((r) => r.claim !== null && !CLOSED.has(r.item.status))
    if (holding?.claim) {
      return (
        <>
          <span class="who">{holding.claim.session?.name ?? holding.claim.agent}</span> is on {holding.item.qualifiedRef}
        </>
      )
    }
    const status = props.run.item.status
    if (status === 'done') {
      const closedAt = b?.rows[0]?.closedAt
      return closedAt ? `Closed ${day(closedAt)}` : 'Closed'
    }
    if (status === 'cancelled' || status === 'superseded') return refState(props.run.item).label
    const tt = t()
    return tt ? `${tt.idle + tt.wait} open` : `${props.run.rollup.open} open`
  }

  return (
    <A class={`strip${closed() ? ' closed' : ''}`} href={runHref(props.run.item.id)}>
      <div>
        <div class="strip-t" title={props.run.item.title}>
          {short(props.run.item.title)}
        </div>
        <div class="strip-m">{meta()}</div>
      </div>
      <Show when={t()} fallback={<div class="segs" />}>
        {(tt) => <Segs leaves={tt().leaves} />}
      </Show>
      <div class="strip-n">
        <strong>{t()?.done ?? props.run.rollup.done}</strong>
        <span> / {t()?.total ?? props.run.rollup.total}</span>
      </div>
    </A>
  )
}
