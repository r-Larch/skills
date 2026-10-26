import { For, Index, Show, createSignal } from 'solid-js'
import { A } from '@solidjs/router'
import { setArchived, type Claim, type ProjectOverview, type RunOverview } from '../api.ts'
import { boards, inbox, overview, refreshOverview } from '../store.ts'
import { CLOSED, refState, tally } from '../state.ts'
import { backlogHref, itemHref, projectHref, runHref } from '../nav.ts'
import { honestyCells, openTasks } from '../inbox.ts'
import { byActivity, runIsOpen } from '../projects.ts'
import { UNKNOWN, ago, clock, day, plural, projectName, projectTagline, short } from '../format.ts'
import { Dot, Known, Legend, ProjectName, Segs } from '../ui.tsx'
import { InboxRowView } from './Inbox.tsx'

const INBOX_SHOWN = 5

export default function Overview() {
  const [showArchived, setShowArchived] = createSignal(false)
  const live = () => (overview()?.projects ?? []).filter((p) => !p.archivedAt).sort(byActivity)
  const archived = () => (overview()?.projects ?? []).filter((p) => p.archivedAt).sort(byActivity)

  const sub = () => {
    const o = overview()
    if (o === undefined) return 'Loading…'
    const open = openTasks(o.projects)
    const n = o.inboxCount
    const projects = `${o.projects.length} ${plural(o.projects.length, 'project')}${
      archived().length > 0 ? ` (${archived().length} archived)` : ''
    }`
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
                <>
                  <Index each={box().rows.slice(0, INBOX_SHOWN)} fallback={<div class="empty">Nothing is waiting on you.</div>}>
                    {(row) => <InboxRowView row={row()} asOf={box().asOf} />}
                  </Index>
                  <Show when={box().rows.length > INBOX_SHOWN}>
                    <A class="row-more" href="/inbox">
                      +{box().rows.length - INBOX_SHOWN} more in the inbox
                    </A>
                  </Show>
                </>
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
            <>
              <Index
                each={live()}
                fallback={
                  <div class="panel empty">
                    {o().projects.length === 0 ? 'No project has been created yet.' : 'Every project is archived.'}
                  </div>
                }
              >
                {(project) => <ProjectPanel project={project()} />}
              </Index>
              <Show when={archived().length > 0}>
                <button type="button" class="archived-link" aria-expanded={showArchived()} onClick={() => setShowArchived(!showArchived())}>
                  {showArchived()
                    ? `Hide ${archived().length} archived ${plural(archived().length, 'project')}`
                    : `${archived().length} archived ${plural(archived().length, 'project')}`}
                </button>
                <Show when={showArchived()}>
                  <Index each={archived()}>{(project) => <ProjectPanel project={project()} />}</Index>
                </Show>
              </Show>
            </>
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

/** One project's card — the overview's panel, and the whole of the project page. */
export function ProjectPanel(props: { project: ProjectOverview }) {
  const tagline = () => projectTagline(props.project.item)
  const open = () => props.project.runs.filter(runIsOpen)
  const closed = () => props.project.runs.filter((r) => !runIsOpen(r))
  const [showClosed, setShowClosed] = createSignal(false)

  return (
    <div class={`panel runs${props.project.archivedAt ? ' archived' : ''}`}>
      <div class="proj-h">
        <div>
          <h2>
            <A href={projectHref(props.project.item.id)}>
              <ProjectName project={props.project.item} />
            </A>
          </h2>
          <Show when={tagline()}>
            <div class="strip-m">{tagline()}</div>
          </Show>
          <Show when={props.project.archivedAt}>{(at) => <div class="strip-m">Archived {day(at())}</div>}</Show>
        </div>
        <span class="n">
          {open().length} open, {closed().length} closed
        </span>
        <A class="backlink" href={backlogHref(props.project.item.id)}>
          Backlog
        </A>
        <ArchiveControl project={props.project} />
      </div>
      <Index each={open()} fallback={<Show when={closed().length === 0}><div class="empty">No runs yet.</div></Show>}>
        {(run) => <RunStrip run={run()} />}
      </Index>
      <Show when={closed().length > 0}>
        <button type="button" class="row-more runs-more" aria-expanded={showClosed()} onClick={() => setShowClosed(!showClosed())}>
          {showClosed()
            ? `Hide ${closed().length} closed ${plural(closed().length, 'run')}`
            : `Show ${closed().length} closed ${plural(closed().length, 'run')}`}
        </button>
        <Show when={showClosed()}>
          <Index each={closed()}>{(run) => <RunStrip run={run()} />}</Index>
        </Show>
      </Show>
    </div>
  )
}

/**
 * The operator's archive switch. Archiving asks first and says what it does; unarchiving only
 * brings the project back, so it does not.
 */
function ArchiveControl(props: { project: ProjectOverview }) {
  const [confirming, setConfirming] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()

  const write = async (archived: boolean) => {
    setBusy(true)
    setError(undefined)
    try {
      await setArchived(props.project.item.id, archived)
      setConfirming(false)
      await refreshOverview()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Show
        when={props.project.archivedAt}
        fallback={
          <button type="button" class="proj-act" disabled={busy() || confirming()} onClick={() => setConfirming(true)}>
            Archive
          </button>
        }
      >
        <button type="button" class="proj-act" disabled={busy()} onClick={() => void write(false)}>
          Unarchive
        </button>
      </Show>
      <Show when={confirming()}>
        <div class="confirm" role="alertdialog" aria-label={`Archive ${projectName(props.project.item)}?`}>
          <p>
            Archive <strong>{projectName(props.project.item)}</strong>? It leaves the sidebar's active lists and this
            overview, behind <em>Archived</em>. Nothing is deleted: agents still open it by its ref and its repo, new work
            for it unarchives it, and you can unarchive it here at any time.
          </p>
          <div class="confirm-b">
            <button type="button" class="proj-act danger" disabled={busy()} onClick={() => void write(true)}>
              Archive
            </button>
            <button type="button" class="proj-act" disabled={busy()} onClick={() => setConfirming(false)}>
              Cancel
            </button>
          </div>
        </div>
      </Show>
      <Show when={error()}>{(message) => <div class="failure confirm-err">Couldn't change it: {message()}</div>}</Show>
    </>
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
