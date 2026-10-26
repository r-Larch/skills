import { For, Show, createEffect, on as onChange, type ParentProps } from 'solid-js'
import { A, useLocation } from '@solidjs/router'
import { boards, failure, overview } from './store.ts'
import { link } from './live.ts'
import { toggleTheme } from './theme.ts'
import { tally } from './state.ts'
import { runHref } from './nav.ts'
import { clock, short } from './format.ts'
import { ProjectName } from './ui.tsx'
import { ActivityIcon, CheckIcon, InboxIcon, OverviewIcon } from './icons.tsx'
import type { RunOverview } from './api.ts'

export default function Shell(props: ParentProps) {
  const location = useLocation()
  const on = (path: string) => location.pathname === path

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
        <For each={overview()?.projects}>
          {(project) => (
            <>
              <div class="proj" title={project.item.title}>
                <ProjectName project={project.item} />
              </div>
              <For each={project.runs}>
                {(run) => <RunLink run={run} current={location.pathname.startsWith(runHref(run.item.id))} />}
              </For>
            </>
          )}
        </For>

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
      <span class="rl-count">
        {counts() ? `${counts()!.done}/${counts()!.total}` : `${props.run.rollup.done}/${props.run.rollup.total}`}
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
