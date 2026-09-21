import {
  For,
  Show,
  createEffect,
  createMemo,
  createResource,
  createSignal,
  on,
  onCleanup,
  onMount,
} from 'solid-js'
import { A, useParams, useSearchParams } from '@solidjs/router'
import * as api from '../api'
import type { JournalEntry, TreeNode } from '../api'
import { briefLines } from '../brief'
import { dayOf, timeOfDay } from '../clock'
import { dividerIndex, merge, newestFirst, newestSeq, seenKey } from '../journal'
import { pulse } from '../live'
import { itemHref } from '../nav'
import TopBar from '../TopBar'

const JOURNAL_PAGE = 60

/** A bound on one catch-up, so a client that has been away for a month cannot walk the whole table. */
const CATCHUP_PAGES = 10

type Branch = { node: TreeNode; children: TreeNode[] }

/**
 * The flat node list arrives in pre-order, so a container owns every following node deeper than it
 * until one at its own level appears. One pass, no id lookups — `ref` is the only address the
 * payload carries (doc 06 rule 2) and nesting is carried by `depth`.
 */
function branches(nodes: TreeNode[]): { root: TreeNode | undefined; list: Branch[] } {
  const root = nodes[0]
  if (root === undefined) return { root: undefined, list: [] }

  const list: Branch[] = []

  for (const node of nodes.slice(1)) {
    if (node.depth <= root.depth + 1 || list.length === 0) {
      list.push({ node, children: [] })
    } else {
      list[list.length - 1]!.children.push(node)
    }
  }

  return { root, list }
}

export default function RunView() {
  const params = useParams<{ run: string }>()

  return (
    <>
      <TopBar
        subtitle="run"
        crumb={
          <span class="crumb">
            <A href="/">← watch floor</A>
          </span>
        }
      />
      {/*
        Keyed, and that is the fix rather than a tidy-up: the router re-renders this route on a param
        change without remounting it, so one instance could outlive the run it was built for. Two
        things fell out of that — a cleanup that read `props.run` after the route had moved on and
        wrote `workstate.seen.undefined`, and a journal that would have appended a second run's events
        to the first run's list. Keying gives every run its own instance, and `run` a plain string.
      */}
      <Show when={params.run} keyed>
        {(run) => <RunBody run={run} />}
      </Show>
    </>
  )
}

function RunBody(props: { run: string }) {
  // Read once, at creation. Everything below uses this, never `props.run` — a value, not a live read.
  const run = props.run

  const [search] = useSearchParams<{ item?: string }>()

  // A rejected resource re-throws on read; the pane should say what went wrong, not vanish.
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

  const [tree] = createResource(() => guard(() => api.tree(run)))
  const [brief] = createResource(() => guard(() => api.brief(run)))

  const shape = createMemo(() => branches(tree()?.nodes ?? []))
  const selected = () => search.item

  return (
    <>
      <div class="wrap">
        <div class="sec-row">
          <h2 class="sec">{run}</h2>
          <span class="count">
            {tree()?.title ?? ''}
            {selected() ? ` · ${selected()}` : ''}
          </span>
          <Show when={failed() !== ''}>
            <span class="count" style={{ color: 'var(--color-attention)' }}>
              {failed()}
            </span>
          </Show>
        </div>

        <div class="reading">
          <div class="tree">
            <p class="paneh">
              <Show when={shape().root} fallback="Tree">
                {(root) => (
                  <>
                    {root().kind} · {root().doneCount} of {root().totalCount} done
                    <Show when={root().reviewCount > 0}> · {root().reviewCount} in review</Show>
                  </>
                )}
              </Show>
            </p>
            <For each={shape().list}>
              {(b) => <BranchRows branch={b} run={run} selected={selected()} />}
            </For>
            <p class="note" style={{ 'font-size': '11.5px', 'margin-top': '12px' }}>
              ⚠ marks a container its gate closed while children stayed open. The status and the
              count disagree, so both are shown.
            </p>
          </div>

          <div class="brief">
            <Show when={brief()} fallback={<pre>…</pre>}>
              {(text) => (
                <pre>
                  <For each={briefLines(text())}>
                    {(line, index) => (
                      <>
                        {index() > 0 ? '\n' : ''}
                        <span class={line.tone === '' ? undefined : `bl-${line.tone}`}>
                          {line.text}
                        </span>
                      </>
                    )}
                  </For>
                </pre>
              )}
            </Show>
            <p class="verbatim">
              The reply to <code>work_brief</code>, byte for byte. The agent working this run is
              reading these exact lines.
            </p>
          </div>

          <JournalPane run={run} />
        </div>
      </div>
    </>
  )
}

function BranchRows(props: { branch: Branch; run: string; selected: string | undefined }) {
  const holdsSelection = () =>
    props.selected !== undefined &&
    (props.branch.node.ref === props.selected ||
      props.branch.children.some((c) => c.ref === props.selected))

  const [open, setOpen] = createSignal(holdsSelection())

  createEffect(() => {
    if (holdsSelection()) setOpen(true)
  })

  return (
    <>
      <Row
        node={props.branch.node}
        run={props.run}
        selected={props.selected}
        indent={0}
        twist={props.branch.children.length > 0 ? (open() ? '▾' : '▸') : undefined}
        onToggle={props.branch.children.length > 0 ? () => setOpen((o) => !o) : undefined}
      />
      <Show when={open()}>
        <For each={props.branch.children}>
          {(child) => (
            <Row
              node={child}
              run={props.run}
              selected={props.selected}
              indent={child.depth - props.branch.node.depth}
            />
          )}
        </For>
      </Show>
    </>
  )
}

function Row(props: {
  node: TreeNode
  run: string
  selected: string | undefined
  indent: number
  twist?: string
  onToggle?: () => void
}) {
  let element: HTMLDivElement | undefined
  const current = () => props.node.ref === props.selected

  onMount(() => {
    // The tree is 200-odd rows on the real corpus; a highlight below the fold is no highlight.
    if (current()) element?.scrollIntoView({ block: 'nearest' })
  })

  const rollup = () => (props.node.totalCount > 0 ? `${props.node.doneCount}/${props.node.totalCount}` : '—')

  return (
    <div
      ref={element}
      class={`tnode ${current() ? 'cur' : ''} ${props.indent > 0 ? 'kid' : ''}`}
      style={props.indent > 1 ? { 'padding-left': `${props.indent * 14}px` } : undefined}
    >
      <span class="nm">
        <Show when={props.twist}>
          <button
            class="twist"
            style={{ background: 'transparent', border: 0, cursor: 'pointer', padding: 0 }}
            aria-expanded={props.twist === '▾'}
            onClick={() => props.onToggle?.()}
          >
            {props.twist}
          </button>{' '}
        </Show>
        <A href={itemHref(props.run, props.node.ref)} title={`${props.node.ref} ${props.node.title}`}>
          {props.node.ref} {props.node.title}
        </A>
      </span>
      <span class="roll">
        {rollup()}
        <Show when={props.node.reviewCount > 0}>
          {' '}
          <span title={`${props.node.reviewCount} in review`}>+{props.node.reviewCount}r</span>
        </Show>
        <Show when={props.node.disagrees}>
          {' '}
          <span
            class="warnmark"
            title={`${props.node.status} with ${props.node.totalCount - props.node.doneCount} of ${props.node.totalCount} children not done`}
          >
            ⚠
          </span>
        </Show>
      </span>
      <span class={`st ${props.node.status}`}>{props.node.status}</span>
    </div>
  )
}

/**
 * Right is time. The divider is anchored to the seq this visit started with, not to the newest event:
 * it must not move under the operator's eyes while they are reading what it separates.
 */
function JournalPane(props: { run: string }) {
  // One string, computed at creation and used for both the read and the write. The previous version
  // recomputed it from `props.run` inside `onCleanup`, which runs after the route has changed: it
  // wrote `workstate.seen.undefined`, never matched what it had read, and the divider never rendered.
  const key = seenKey(props.run)
  const run = props.run

  const [events, setEvents] = createSignal<JournalEntry[]>([])
  const [more, setMore] = createSignal(false)
  const [seen, setSeen] = createSignal(0)
  const [failed, setFailed] = createSignal('')

  // Not a signal: the cursor is never rendered, and a tracked read of it inside the effect that
  // advances it is a loop waiting to happen.
  let cursor = 0

  const remember = () => {
    const newest = newestSeq(events())
    if (newest > 0) localStorage.setItem(key, String(newest))
  }

  onMount(() => {
    const stored = Number(localStorage.getItem(key) ?? '0')
    setSeen(Number.isFinite(stored) ? stored : 0)

    // Closing the tab is the commonest way to stop looking, and it never unmounts the component.
    addEventListener('pagehide', remember)
    onCleanup(() => removeEventListener('pagehide', remember))
  })

  onCleanup(remember)

  // First load, then one delta per pulse. `since=` is U6's reconnect contract: a frame that says
  // only "seq 127 happened" is answered by asking for everything after the last seq we hold. A
  // truncated catch-up page is drained here rather than left for the next frame, because the next
  // frame may not come — the events that arrived while we were away do not produce one.
  createEffect(
    on(pulse, () => {
      void (async () => {
        try {
          for (let page = 0; page < CATCHUP_PAGES; page += 1) {
            const answer = await api.journal(run, cursor, JOURNAL_PAGE)
            setFailed('')
            cursor = answer.cursor

            if (answer.events.length > 0) {
              setEvents((held) => merge(held, answer.events))
            }

            setMore(answer.hasMore)
            if (!answer.hasMore) return
          }
        } catch (error) {
          if (!(error instanceof api.Unauthorized)) setFailed(String(error))
        }
      })()
    }),
  )

  const rows = createMemo(() => newestFirst(events()))
  const divider = createMemo(() => dividerIndex(rows(), seen()))

  return (
    <div class="journal">
      <p class="paneh">
        Journal · {events().length} events
        <Show when={more()}> · more waiting</Show>
      </p>

      <Show
        when={events().length > 0}
        fallback={<p class="empty" style={{ padding: '0' }}>Nothing recorded for this run yet.</p>}
      >
        <For each={rows()}>
          {(event, index) => (
            <>
              <Show when={divider() === index()}>
                <div class="jdiv">since you last looked</div>
              </Show>
              <div class="jrow" title={`seq ${event.seq} · ${event.kind} · ${event.actor} (${event.role}) · ${dayOf(event.at)} ${event.at}`}>
                <span class="t">{timeOfDay(event.at)}</span>
                <span class="s">
                  <Show when={event.ref}>
                    <span class="jref">{event.ref}</span>{' '}
                  </Show>
                  {event.summary ?? event.kind}
                </span>
              </div>
            </>
          )}
        </For>

        {/* Every loaded event is newer than the cursor, so the line belongs below all of them. */}
        <Show when={divider() === rows().length}>
          <div class="jdiv">since you last looked</div>
        </Show>
      </Show>

      <Show when={failed() !== ''}>
        <p class="hnote">{failed()}</p>
      </Show>
    </div>
  )
}
