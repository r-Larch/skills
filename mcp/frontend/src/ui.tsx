import { For, Show, createSignal, onCleanup, type Accessor, type JSX } from 'solid-js'
import { A } from '@solidjs/router'
import type { BoardRow, ItemRef } from './api.ts'
import { rowState, type State, type StateKey } from './state.ts'
import { UNKNOWN, projectName, projectRefIfDifferent } from './format.ts'
import { copyValue } from './nav.ts'
import { CopyIcon } from './icons.tsx'

export const Dot = (props: { k: StateKey | 'note'; title?: string; style?: JSX.CSSProperties }) => (
  <span class={`dot s-${props.k}`} title={props.title} style={props.style} />
)

export const Pill = (props: { state: State }) => (
  <span class={`pill p-${props.state.k}`}>{props.state.label}</span>
)

/** The project's name, and its ref in small mono type when the name differs from it. */
export const ProjectName = (props: { project: { title: string; ref: string } }) => (
  <>
    {projectName(props.project)}
    <Show when={projectRefIfDifferent(props.project)}>{(ref) => <span class="pref">{ref()}</span>}</Show>
  </>
)

/**
 * One-click copy of an item's full path (doc 13), `copyValue()`, whatever the ref beside it shows.
 * It often sits inside a row link, so the click is taken on the button itself and never reaches the
 * link or the router's document listener.
 */
export function CopyRef(props: { item: Pick<ItemRef, 'fullRef'> }) {
  const value = () => copyValue(props.item)
  const [done, setDone] = createSignal<'copied' | 'selected'>()
  let picked: HTMLSpanElement | undefined
  let timer: ReturnType<typeof setTimeout> | undefined
  onCleanup(() => clearTimeout(timer))

  const show = (state: 'copied' | 'selected') => {
    clearTimeout(timer)
    setDone(state)
    timer = setTimeout(() => setDone(undefined), state === 'copied' ? 1600 : 8000)
  }

  const copy = async () => {
    try {
      // A clipboard write can stay pending with no error (a held permission, an unfocused
      // window); without a deadline the button would show nothing at all.
      await Promise.race([
        navigator.clipboard.writeText(value()),
        new Promise((_, reject) => setTimeout(() => reject(new Error('clipboard timeout')), 1200)),
      ])
      show('copied')
    } catch {
      show('selected')
      if (picked === undefined) return
      const range = document.createRange()
      range.selectNodeContents(picked)
      const selection = window.getSelection()
      selection?.removeAllRanges()
      selection?.addRange(range)
    }
  }

  const click = (event: MouseEvent) => {
    event.preventDefault()
    event.stopPropagation()
    void copy()
  }

  return (
    <span class="copy-ref">
      <span class="copy-strut" aria-hidden="true">{'​'}</span>
      <button
        type="button"
        class={`copy${done() === 'copied' ? ' done' : ''}`}
        aria-label={`Copy ref ${value()}`}
        title={`Copy ${value()}`}
        ref={(el) => el.addEventListener('click', click)}
      >
        {done() === 'copied' ? 'Copied' : <CopyIcon />}
      </button>
      <Show when={done() === 'selected'}>
        <span class="copy-sel" ref={picked} title="The clipboard is unavailable here; press Ctrl+C">
          {value()}
        </span>
      </Show>
      <span class="sr-only" role="status">
        {done() === 'copied' ? `Copied ${value()}` : done() === 'selected' ? `${value()} selected, press Ctrl+C` : ''}
      </span>
    </span>
  )
}

/** The quiet link that shows or hides cancelled work; `href` is the page with the flag flipped. */
export const CancelledToggle = (props: { count: number; shown: boolean; href: string; class?: string }) => (
  <Show when={props.count > 0}>
    <A class={`toggle-cancelled${props.class ? ` ${props.class}` : ''}`} href={props.href} noScroll>
      {props.shown ? `Hide ${props.count} cancelled` : `Show ${props.count} cancelled`}
    </A>
  </Show>
)

/** One segment per leaf, in board order. Gone leaves are already out of `leaves`. */
export const Segs = (props: { leaves: BoardRow[]; style?: JSX.CSSProperties }) => (
  <div class="segs" style={props.style}>
    <For each={props.leaves} fallback={<span class="strip-m">No tasks yet</span>}>
      {(leaf) => {
        const s = rowState(leaf)
        return <i class={s.k} title={`${leaf.item.qualifiedRef} · ${leaf.item.title} — ${s.label}`} />
      }}
    </For>
  </div>
)

/**
 * Not asked yet is a dash, never the empty sentence: only an answer may say "nothing". The child
 * is built once and reads the accessor, so a live re-read updates rows in place.
 */
export function Known<T extends object>(props: {
  value: T | undefined
  children: (value: Accessor<T>) => JSX.Element
  class?: string
}) {
  return (
    <Show when={props.value} fallback={<div class={props.class ?? 'empty'}>{UNKNOWN}</div>}>
      {(value) => props.children(value)}
    </Show>
  )
}

export const Legend = () => (
  <div class="legend">
    <span><i style={{ background: 'var(--proven)' }} />Verified</span>
    <span><i style={{ background: 'var(--claimed)' }} />Done, not verified</span>
    <span><i style={{ background: 'var(--doing)' }} />In progress</span>
    <span><i style={{ background: 'var(--stop)' }} />Blocked</span>
    <span><i style={{ 'box-shadow': 'inset 0 0 0 1.5px var(--idle)' }} />Waiting on another task</span>
    <span><i style={{ background: 'var(--idle)' }} />Open</span>
  </div>
)
