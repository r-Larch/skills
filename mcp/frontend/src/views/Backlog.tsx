import { For, Show } from 'solid-js'
import { A, useParams, useSearchParams } from '@solidjs/router'
import * as api from '../api.ts'
import type { ItemRef } from '../api.ts'
import { load, overview } from '../store.ts'
import { SHOW_CANCELLED, backlogHref, itemHref, runHref } from '../nav.ts'
import { LEAF, refState } from '../state.ts'
import { plural, short } from '../format.ts'
import { CancelledToggle, CopyRef, Dot, Known, ProjectName } from '../ui.tsx'

export default function Backlog() {
  const params = useParams<{ project: string }>()
  const [search] = useSearchParams<{ cancelled?: string }>()
  const page = load(() => params.project, api.backlog)
  const project = () => overview()?.projects.find((p) => p.item.id === params.project)
  const shown = () => search.cancelled === SHOW_CANCELLED

  const count = () => page.data()?.groups.reduce((n, g) => n + g.items.length, 0) ?? 0

  /** The backlog read lists open work only; cancelled leaves come off the backlog run's board. */
  const runId = () => page.data()?.run?.id
  const cancelledCount = () => project()?.runs.find((r) => r.item.id === runId())?.rollup.cancelled ?? 0
  const board = load(() => (shown() ? runId() : undefined), api.board)
  const cancelledItems = () =>
    (board.data()?.rows ?? []).filter((r) => LEAF.has(r.item.kind) && r.item.status === 'cancelled').map((r) => r.item)
  const toggle = () => `${backlogHref(params.project)}${shown() ? '' : `?cancelled=${SHOW_CANCELLED}`}`

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <div class="crumbs">
            <A href="/">Overview</A> /{' '}
            <Show when={project()?.item} fallback="project">
              {(p) => (
                <>
                  <ProjectName project={p()} /> <CopyRef item={p()} />
                </>
              )}
            </Show>
          </div>
          <h1>Backlog</h1>
          <div class="sub">
            <Show when={page.data()} fallback="Loading…">
              {(b) =>
                b().run
                  ? `${count()} open ${plural(count(), 'item')} deferred out of other runs, grouped by where ${count() === 1 ? 'it' : 'they'} came from.`
                  : 'This project has no backlog run: nothing has been deferred to it.'
              }
            </Show>
          </div>
        </div>
        <Show when={page.data()?.run}>
          {(run) => (
            <A class="stamp" href={runHref(run().id)}>
              Open the backlog run
            </A>
          )}
        </Show>
      </div>

      <Show when={page.missing()}>
        <div class="panel empty">There is no project with this id.</div>
      </Show>
      <Show when={page.error()}>{(e) => <div class="failure">{e()}</div>}</Show>

      <Show when={!page.missing()}>
        <Known value={page.data()} class="panel empty">
          {(b) => (
            <div class="panel" style={{ 'padding-bottom': '8px' }}>
              <For each={b().groups} fallback={<div class="empty">The backlog is empty.</div>}>
                {(group) => (
                  <>
                    <Show
                      when={group.origin}
                      fallback={<div class="group-h">Deferred from nowhere recorded</div>}
                    >
                      {(origin) => (
                        <A class="group-h" href={itemHref(origin(), null)}>
                          From {short(origin().title)}
                          <span class="gc">
                            {origin().qualifiedRef} <CopyRef item={origin()} />
                          </span>
                        </A>
                      )}
                    </Show>
                    <For each={group.items}>{(item) => <BacklogItem item={item} run={b().run} />}</For>
                  </>
                )}
              </For>
              <Show when={shown() && cancelledItems().length > 0}>
                <div class="group-h">Cancelled</div>
                <For each={cancelledItems()}>{(item) => <BacklogItem item={item} run={b().run} />}</For>
              </Show>
              <CancelledToggle count={cancelledCount()} shown={shown()} href={toggle()} class="pad" />
            </div>
          )}
        </Known>
      </Show>
    </div>
  )
}

function BacklogItem(props: { item: ItemRef; run: ItemRef | null }) {
  return (
    <A class={`item${props.item.status === 'cancelled' ? ' gone' : ''}`} href={itemHref(props.item, props.run)}>
      <Dot k={refState(props.item).k} />
      <div class="item-t">
        <span class="item-ref">{props.item.qualifiedRef}</span>
        <CopyRef item={props.item} />
        {props.item.title}
      </div>
      <div class="item-side">{props.item.kind}</div>
    </A>
  )
}
