import { For, Show, createEffect, createSignal, on as onChange, onCleanup, onMount, type ParentProps } from 'solid-js'
import { A, useLocation } from '@solidjs/router'
import { account, boards, failure, overview } from './store.ts'
import { link } from './live.ts'
import { toggleTheme } from './theme.ts'
import { countTitle, counted, tally } from './state.ts'
import { ACCOUNT, ADMIN, projectHref, runHref } from './nav.ts'
import { clock, short } from './format.ts'
import { ProjectName } from './ui.tsx'
import { ActivityIcon, AdminIcon, CheckIcon, InboxIcon, MenuIcon, OverviewIcon, SearchIcon, SessionsIcon } from './icons.tsx'
import { PHONE_QUERY, closesOnMove, pageName } from './menu.ts'
import { showSwitcher } from './admin.ts'
import { switchFailure, switchWorkspace } from './tenant.ts'
import Palette, { openPalette } from './Palette.tsx'
import { chordLabel } from './palette.ts'
import {
  archivedOpen,
  byActivity,
  matchesQuery,
  projectGroup,
  runIsOpen,
  setArchivedOpen,
  type ProjectGroup,
} from './projects.ts'
import { signOut, type ProjectOverview, type RunOverview } from './api.ts'

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

  // Below 760px the sidebar is an overlay behind the top bar's Menu button.
  const [menu, setMenu] = createSignal(false)
  let menuButton: HTMLButtonElement | undefined
  let side: HTMLElement | undefined

  const openMenu = () => {
    setMenu(true)
    // A frame later: until the overlay is styled visible, focus() on its first link does nothing.
    requestAnimationFrame(() => side?.querySelector<HTMLElement>('a[href], button, input, select')?.focus())
  }
  const closeMenu = (refocus = true) => {
    if (!menu()) return
    setMenu(false)
    if (refocus) queueMicrotask(() => menuButton?.focus())
  }

  createEffect(
    onChange(
      () => location.pathname + location.search,
      (to, from) => {
        if (from !== undefined && closesOnMove(from, to)) closeMenu()
      },
    ),
  )

  onMount(() => {
    // Captured on the document: under the palette's window capture, which stops an Escape meant for
    // it, and ahead of a run sheet's own Escape, so one press closes one thing.
    const key = (e: KeyboardEvent) => {
      if (menu() && e.key === 'Escape') {
        e.stopPropagation()
        closeMenu()
      }
    }
    const phone = window.matchMedia(PHONE_QUERY)
    const widened = () => {
      if (!phone.matches) closeMenu(false)
    }
    document.addEventListener('keydown', key, true)
    phone.addEventListener('change', widened)
    onCleanup(() => {
      document.removeEventListener('keydown', key, true)
      phone.removeEventListener('change', widened)
    })
  })

  return (
    <div class={`app${menu() ? ' menu-open' : ''}`}>
      <header class="topbar" inert={menu() || undefined}>
        <button
          type="button"
          class="topbar-menu"
          aria-label="Menu"
          aria-controls="side"
          aria-expanded={menu()}
          ref={menuButton}
          onClick={openMenu}
        >
          <MenuIcon />
        </button>
        <A href="/" class="brand-mark" aria-label="WorkState overview">
          <CheckIcon size={16} weight={2.6} />
        </A>
        <span class="topbar-page">{pageName(location.pathname, overview()?.projects)}</span>
        <button
          type="button"
          class="topbar-find"
          aria-label="Search refs, items, commands"
          aria-haspopup="dialog"
          aria-keyshortcuts="Control+P Meta+P /"
          onClick={openPalette}
        >
          <SearchIcon />
          <kbd>{chordLabel(navigator.userAgent)}</kbd>
        </button>
      </header>

      <Show when={menu()}>
        <div class="scrim" aria-hidden="true" onClick={() => closeMenu()} />
      </Show>

      <aside class="side" id="side" ref={side}>
        <A href="/" class="brand">
          <span class="brand-mark">
            <CheckIcon size={16} weight={2.6} />
          </span>
          <span class="brand-name">WorkState</span>
        </A>

        <TenantSwitcher />

        <button
          type="button"
          class="side-find"
          aria-haspopup="dialog"
          aria-keyshortcuts="Control+P Meta+P /"
          onClick={openPalette}
        >
          <SearchIcon />
          <span class="side-find-t">Search refs, items, commands</span>
          <kbd>{chordLabel(navigator.userAgent)}</kbd>
        </button>

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
          <A href="/sessions" class={on('/sessions') ? 'on' : ''}>
            <SessionsIcon />
            Sessions
          </A>
          <Show when={account()?.user.administers}>
            <A href={ADMIN} class={location.pathname.startsWith(ADMIN) ? 'on' : ''}>
              <AdminIcon />
              Admin
            </A>
          </Show>
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
        <AccountMenu />
      </aside>

      <main class="main" ref={main} inert={menu() || undefined}>
        <Show when={failure()}>
          {(message) => (
            <div class="page" style={{ 'padding-bottom': 0 }}>
              <div class="failure">Couldn't reach the API: {message()}</div>
            </div>
          )}
        </Show>
        {props.children}
      </main>

      <Palette />
    </div>
  )
}

/** The signed-in user's name, opening Change password and Sign out. */
function AccountMenu() {
  const [open, setOpen] = createSignal(false)
  return (
    <Show when={account()}>
      {(a) => (
        <div class="side-account">
          <button type="button" class="side-toggle" aria-expanded={open()} onClick={() => setOpen(!open())}>
            <span class="caret">{open() ? '▾' : '▸'}</span>
            <span class="side-account-n">{a().user.name}</span>
          </button>
          <Show when={open()}>
            <div class="side-account-m">
              <A href={ACCOUNT} onClick={() => setOpen(false)}>
                Change password
              </A>
              <button type="button" onClick={() => void signOut()}>
                Sign out
              </button>
            </div>
          </Show>
        </div>
      )}
    </Show>
  )
}

/**
 * The workspace the session acts in, as a choice among the ones the user may enter — shown only when there is
 * more than one. Switching re-issues the cookie (`POST /api/session/tenant`) and reloads at the overview.
 */
function TenantSwitcher() {
  const tenants = () => account()?.tenants ?? []
  const current = () => tenants().find((t) => t.current)
  return (
    <Show when={showSwitcher(tenants())}>
      <label class="side-tenant">
        <span>Workspace</span>
        <select
          value={current()?.id ?? ''}
          aria-label="Switch workspace"
          onChange={(e) => {
            const to = e.currentTarget.value
            if (to !== current()?.id) void switchWorkspace(to)
          }}
        >
          <For each={tenants()}>{(t) => <option value={t.id}>{t.slug}</option>}</For>
        </select>
      </label>
      <Show when={switchFailure()}>{(message) => <div class="side-none">{message()}</div>}</Show>
    </Show>
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
