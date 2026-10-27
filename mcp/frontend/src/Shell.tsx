import { For, Show, createEffect, createSignal, on as onChange, type ParentProps } from 'solid-js'
import { A, useLocation } from '@solidjs/router'
import { boards, failure, overview } from './store.ts'
import { link } from './live.ts'
import { toggleTheme } from './theme.ts'
import { countTitle, counted, tally } from './state.ts'
import { projectHref, runHref } from './nav.ts'
import { clock, short } from './format.ts'
import { ProjectName } from './ui.tsx'
import { ActivityIcon, CheckIcon, InboxIcon, OverviewIcon } from './icons.tsx'
import {
  archivedOpen,
  byActivity,
  matchesQuery,
  projectGroup,
  runIsOpen,
  setArchivedOpen,
  type ProjectGroup,
} from './projects.ts'
import type { ProjectOverview, RunOverview } from './api.ts'

export default function Shell(props: ParentProps) {
  const location = useLocation()
  const on = (path: string) => location.pathname === path
  const [query, setQuery] = createSignal('')

  // The page scrolls inside <main>, which the router's own scroll-to-top never touches. Moving
  // between items of one run keeps the plan where it is; any other move starts at the top.
  let main: HTMLElement | undefined
  createEffect(
    onChange(
      () => location.pathname.split('/').slice(0, 3).join('/'),
      () => {
        if (main) main.scrollTop = 0
      },
      { defer: true },
    ),
  )

  return (
    <div class="app">
      <aside class="side">
        <A href="/" class="brand">
          <span class="brand-mark">
            <CheckIcon size={16} weight={2.6} />
          </span>
          <span class="brand-name">WorkState</span>
        </A>

        <nav class="nav">
          <A href="/" class={on('/') ? 'on' : ''}>
            <OverviewIcon />
            Overview
          </A>
          <A href="/inbox" class={on('/inbox') ? 'on' : ''}>
            <InboxIcon />
            Needs you
            <Show when={(overview()?.inboxCount ?? 0) > 0}>
              <span class="badge">{overview()!.inboxCount}</span>
            </Show>
          </A>
          <A href="/activity" class={on('/activity') ? 'on' : ''}>
            <ActivityIcon />
            Activity
          </A>
        </nav>

        <div class="side-h">Projects</div>
        <input
          class="side-search"
          type="search"
          placeholder="Find a project"
          aria-label="Find a project by name or ref"
          value={query()}
          onInput={(e) => setQuery(e.currentTarget.value)}
        />
        <Show when={overview()}>
          {(o) => {
            const shown = () => [...o().projects].filter((p) => matchesQuery(p, query())).sort(byActivity)
            const group = (g: ProjectGroup) => shown().filter((p) => projectGroup(p, o().asOf) === g)
            const searching = () => query().trim() !== ''
            return (
              <>
                <SideGroup label="Active" projects={group('active')} path={location.pathname} />
                <SideGroup label="Quiet" projects={group('quiet')} path={location.pathname} />
                <Show when={group('archived').length > 0}>
                  <button
                    type="button"
                    class="side-toggle"
                    aria-expanded={archivedOpen() || searching()}
                    onClick={() => setArchivedOpen(!archivedOpen())}
                  >
                    <span class="caret">{archivedOpen() || searching() ? '▾' : '▸'}</span>
                    Archived ({group('archived').length})
                  </button>
                  <Show when={archivedOpen() || searching()}>
                    <SideGroup projects={group('archived')} path={location.pathname} />
                  </Show>
                </Show>
                <Show when={searching() && shown().length === 0}>
                  <div class="side-none">No project matches “{query().trim()}”.</div>
                </Show>
              </>
            )
          }}
        </Show>

        <div class="side-foot">
          <span class={`link-state ${link()}`} title={`Live updates: ${link()}`}>
            <i />
            <Show when={overview()} fallback="Loading…">
              {(o) => `As of ${clock(o().asOf)}`}
            </Show>
          </span>
          <button type="button" onClick={toggleTheme}>
            Theme
          </button>
        </div>
      </aside>

      <main class="main" ref={main}>
        <Show when={failure()}>
          {(message) => (
            <div class="page" style={{ 'padding-bottom': 0 }}>
              <div class="failure">Couldn't reach the API: {message()}</div>
            </div>
          )}
        </Show>
        {props.children}
      </main>
    </div>
  )
}

/**
 * One group of the sidebar. A project lists its open runs only; the rest are one "n closed" row
 * that opens the project page, where they sit behind "Show n closed runs".
 */
function SideGroup(props: { label?: string; projects: ProjectOverview[]; path: string }) {
  return (
    <Show when={props.projects.length > 0}>
      <Show when={props.label}>
        <div class="side-g">{props.label}</div>
      </Show>
      <For each={props.projects}>
        {(project) => {
          const open = () => project.runs.filter(runIsOpen)
          const closed = () => project.runs.length - open().length
          const href = () => projectHref(project.item.id)
          return (
            <>
              <A
                href={href()}
                class={`proj${project.archivedAt ? ' archived' : ''}${props.path === href() ? ' on' : ''}`}
                title={project.item.title}
              >
                <ProjectName project={project.item} />
              </A>
              <For each={open()}>
                {(run) => <RunLink run={run} current={props.path.startsWith(runHref(run.item.id))} />}
              </For>
              <Show when={closed() > 0}>
                <A href={href()} class="side-closed">
                  {closed()} closed
                </A>
              </Show>
            </>
          )
        }}
      </For>
    </Show>
  )
}

function RunLink(props: { run: RunOverview; current: boolean }) {
  const counts = () => {
    const board = boards()[props.run.item.id]
    if (board === undefined) return null
    return tally(board.rows)
  }
  const width = (k: 'proven' | 'claimed' | 'doing' | 'stop') => {
    const t = counts()
    return t && t.total > 0 ? `${(t[k] / t.total) * 100}%` : '0'
  }
  const closed = () => ['done', 'cancelled', 'superseded'].includes(props.run.item.status)

  return (
    <A
      href={runHref(props.run.item.id)}
      class={`runlink${props.current ? ' on' : ''}${closed() ? ' closed' : ''}`}
      title={props.run.item.title}
    >
      <span class="rl-title">{short(props.run.item.title)}</span>
      <span class="rl-count" title={countTitle(counts() ?? counted(props.run.rollup))}>
        {`${(counts() ?? counted(props.run.rollup)).done}/${(counts() ?? counted(props.run.rollup)).total}`}
      </span>
      <span class="rl-bar">
        <i style={{ width: width('proven'), background: 'var(--proven)' }} />
        <i style={{ width: width('claimed'), background: 'var(--claimed)' }} />
        <i style={{ width: width('doing'), background: 'var(--doing)' }} />
        <i style={{ width: width('stop'), background: 'var(--stop)' }} />
      </span>
    </A>
  )
}
