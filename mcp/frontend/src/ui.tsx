import { For, Show, type Accessor, type JSX } from 'solid-js'
import type { BoardRow } from './api.ts'
import { rowState, type State, type StateKey } from './state.ts'
import { UNKNOWN, projectName, projectRefIfDifferent } from './format.ts'

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
