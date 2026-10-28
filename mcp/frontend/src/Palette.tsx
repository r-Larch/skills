import { For, Show, createEffect, createMemo, createSignal, on, onCleanup, onMount, type JSX } from 'solid-js'
import { useNavigate } from '@solidjs/router'
import * as api from './api.ts'
import { account, overview } from './store.ts'
import { toggleTheme } from './theme.ts'
import { refState } from './state.ts'
import { Pill, ProjectName } from './ui.tsx'
import { arrange, commands, entryHref, isPaletteChord, isSlash, matchCommands, type Entry, type PaletteAccount } from './palette.ts'
import { switchWorkspace } from './tenant.ts'

const DEBOUNCE_MS = 150

const [open, setOpen] = createSignal(false)

/** Opens the palette from anywhere; the sidebar's search field calls this. */
export const openPalette = () => setOpen(true)

/** Where a key would be typed rather than taken as a shortcut. */
const typingIn = (target: EventTarget | null): boolean =>
  target instanceof HTMLElement &&
  (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))

/**
 * The command palette (doc 13): Ctrl+P / Cmd+P or `/` from any page. Commands match here; items
 * come from `/api/v1/search`, debounced, and an answer for a query no longer typed is dropped.
 * Mounted once, by the shell.
 */
export default function Palette() {
  const navigate = useNavigate()
  const [query, setQuery] = createSignal('')
  const [hits, setHits] = createSignal<api.SearchHit[]>([])
  const [searched, setSearched] = createSignal<string>()
  const [failed, setFailed] = createSignal<string>()
  const [active, setActive] = createSignal(0)
  let input: HTMLInputElement | undefined
  let list: HTMLDivElement | undefined
  let returnTo: HTMLElement | null = null

  const keys = (e: KeyboardEvent) => {
    if (isPaletteChord(e) || (!open() && isSlash(e, typingIn(e.target)))) {
      e.preventDefault()
      if (open()) input?.select()
      else setOpen(true)
      return
    }
    // Captured on the window, so a side panel's own Escape never closes it under the palette.
    if (open() && e.key === 'Escape') {
      e.preventDefault()
      e.stopPropagation()
      setOpen(false)
    }
  }
  // Focus that leaves the dialog while it is open (a screen reader's virtual cursor) comes back.
  const trap = (e: FocusEvent) => {
    if (open() && input && !input.closest('.pal')?.contains(e.target as Node)) input.focus()
  }
  onMount(() => {
    window.addEventListener('keydown', keys, true)
    document.addEventListener('focusin', trap)
  })
  onCleanup(() => {
    window.removeEventListener('keydown', keys, true)
    document.removeEventListener('focusin', trap)
  })

  createEffect(
    on(open, (isOpen) => {
      if (isOpen) {
        returnTo = document.activeElement instanceof HTMLElement ? document.activeElement : null
        queueMicrotask(() => input?.focus())
        return
      }
      setQuery('')
      setHits([])
      setSearched(undefined)
      setFailed(undefined)
      if (returnTo?.isConnected) returnTo.focus()
      returnTo = null
    }),
  )

  // Enter pressed before the answer for the input came back: it acts on that answer, not an older one.
  let enterWhenReady = false
  let ticket = 0
  createEffect(
    on(query, (typed) => {
      const q = typed.trim()
      const mine = ++ticket
      enterWhenReady = false
      if (q === '') {
        setHits([])
        setSearched(undefined)
        setFailed(undefined)
        return
      }
      const timer = setTimeout(() => {
        api.search(q).then(
          (result) => {
            if (mine !== ticket) return
            setHits(result.hits)
            setSearched(q)
            setFailed(undefined)
            if (enterWhenReady) {
              enterWhenReady = false
              run(entries()[active()])
            }
          },
          (error: unknown) => {
            if (mine !== ticket) return
            enterWhenReady = false
            setHits([])
            setSearched(q)
            setFailed(error instanceof Error ? error.message : String(error))
          },
        )
      }, DEBOUNCE_MS)
      onCleanup(() => clearTimeout(timer))
    }),
  )

  const paletteAccount = (): PaletteAccount | undefined => {
    const a = account()
    return a && { administers: a.user.administers, systemAdmin: a.user.systemAdmin, tenants: a.tenants }
  }
  const matched = createMemo(() => matchCommands(commands(overview()?.projects ?? [], paletteAccount()), query()))
  const layout = createMemo(() => arrange(matched(), query().trim() === '' ? [] : hits()))
  const entries = () => layout().entries
  const pending = () => query().trim() !== '' && searched() !== query().trim()

  createEffect(on(layout, () => setActive(0)))

  const optionId = (i: number) => `palette-opt-${i}`
  createEffect(() => {
    const id = optionId(active())
    if (open()) list?.querySelector(`#${id}`)?.scrollIntoView({ block: 'nearest' })
  })

  const run = (entry: Entry | undefined) => {
    if (entry === undefined) return
    const href = entryHref(entry)
    // Focus would otherwise return to a page this navigation is about to replace.
    if (href !== null) returnTo = null
    setOpen(false)
    if (href !== null) navigate(href)
    else if (entry.kind === 'command' && entry.command.action === 'theme') toggleTheme()
    else if (entry.kind === 'command' && entry.command.action === 'tenant' && entry.command.tenant) void switchWorkspace(entry.command.tenant)
  }

  const onKey = (e: KeyboardEvent) => {
    const count = entries().length
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      if (count > 0) setActive((i) => (i + (e.key === 'ArrowDown' ? 1 : count - 1)) % count)
    } else if (e.key === 'Enter') {
      e.preventDefault()
      if (pending() && entries()[active()]?.kind !== 'command') enterWhenReady = true
      else run(entries()[active()])
    } else if (e.key === 'Tab') {
      // The input is the dialog's one focusable element; the options are reached by arrow keys.
      e.preventDefault()
    }
  }

  const indexOf = (entry: Entry) => entries().indexOf(entry)
  const commandEntries = () => entries().filter((e) => e.kind === 'command')
  const itemEntries = () => entries().filter((e) => e.kind === 'item')

  const Option = (props: { entry: Entry; children: JSX.Element }) => {
    const i = () => indexOf(props.entry)
    return (
      <div
        id={optionId(i())}
        role="option"
        class={`pal-opt${i() === active() ? ' on' : ''}`}
        aria-selected={i() === active()}
        onMouseMove={() => setActive(i())}
        onMouseDown={(e) => e.preventDefault()}
        onClick={() => run(props.entry)}
      >
        {props.children}
      </div>
    )
  }

  const Commands = () => (
    <Show when={commandEntries().length > 0}>
      <div role="group" aria-labelledby="palette-g-cmd">
        <div class="pal-g" id="palette-g-cmd" role="presentation">
          Commands
        </div>
        <For each={commandEntries()}>
          {(entry) => (
            <Option entry={entry}>
              <span class="pal-label">{entry.kind === 'command' ? entry.command.label : ''}</span>
            </Option>
          )}
        </For>
      </div>
    </Show>
  )

  const Items = () => (
    <Show when={query().trim() !== ''}>
      <div role="group" aria-labelledby="palette-g-items">
        <div class="pal-g" id="palette-g-items" role="presentation">
          Items
        </div>
        <For each={itemEntries()}>
          {(entry) => {
            if (entry.kind !== 'item') return null
            const { item, project, match, snippet } = entry.hit
            return (
              <Option entry={entry}>
                <span class="pal-ref mono">{item.fullRef}</span>
                <span class="pal-title">
                  {item.title}
                  <Show when={match === 'text' && snippet && snippet !== item.title}>
                    <span class="pal-snip">{snippet}</span>
                  </Show>
                </span>
                <span class="pal-meta">
                  <span class="pal-kind">{item.kind}</span>
                  <Pill state={refState(item)} />
                  <Show when={project && project.id !== item.id ? project : null}>
                    {(p) => (
                      <span class="pal-proj">
                        <ProjectName project={p()} />
                      </span>
                    )}
                  </Show>
                </span>
              </Option>
            )
          }}
        </For>
        <Show when={itemEntries().length === 0}>
          <div class="pal-none" role="presentation">
            <Show
              when={!pending()}
              fallback={`Searching for “${query().trim()}”…`}
            >
              <Show
                when={failed()}
                fallback={
                  <>
                    No item matches “{searched()}”. Search takes a ref — bare (<code>spa-blank-page-seam</code>), as{' '}
                    <code>run/ref</code> (<code>m/12</code>) or <code>project/run/ref</code> — or words from a title.
                  </>
                }
              >
                {(message) => <>Search failed: {message()}</>}
              </Show>
            </Show>
          </div>
        </Show>
      </div>
    </Show>
  )

  return (
    <Show when={open()}>
      <div class="pal-back" onMouseDown={(e) => e.target === e.currentTarget && setOpen(false)}>
        <div
          class="pal"
          role="dialog"
          aria-modal="true"
          aria-labelledby="palette-label"
          onMouseDown={(e) => e.target !== input && e.preventDefault()}
        >
          <label id="palette-label" class="sr-only" for="palette-input">
            Search items by ref or title, or run a command
          </label>
          <input
            id="palette-input"
            ref={input}
            class="pal-input"
            type="text"
            role="combobox"
            aria-expanded="true"
            aria-controls="palette-list"
            aria-autocomplete="list"
            aria-activedescendant={entries().length > 0 ? optionId(active()) : undefined}
            placeholder="Find a ref like m/12, a title word, or a command"
            autocomplete="off"
            spellcheck={false}
            value={query()}
            onInput={(e) => setQuery(e.currentTarget.value)}
            onKeyDown={onKey}
          />
          <div id="palette-list" class="pal-list" role="listbox" aria-label="Results" ref={list}>
            <Show when={layout().itemsFirst} fallback={<><Commands /><Items /></>}>
              <Items />
              <Commands />
            </Show>
          </div>
          <div class="pal-foot" aria-hidden="true">
            <span><kbd>↑</kbd><kbd>↓</kbd> move</span>
            <span><kbd>Enter</kbd> open</span>
            <span><kbd>Esc</kbd> close</span>
          </div>
        </div>
      </div>
    </Show>
  )
}
