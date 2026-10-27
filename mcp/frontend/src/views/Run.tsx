import { For, Index, Show, createEffect, on } from 'solid-js'
import { A, useParams, useSearchParams } from '@solidjs/router'
import * as api from '../api.ts'
import type { Board, BoardRow, ItemRef } from '../api.ts'
import { load } from '../store.ts'
import { CLOSED, progress, rowState, tally } from '../state.ts'
import { FILTERS, asFilter, cancelledLines, plan, planQuery as query, type Filter } from '../plan.ts'
import { SHOW_CANCELLED, boardHref, runHref } from '../nav.ts'
import { claimHolder, short } from '../format.ts'
import { CancelledToggle, CopyRef, Dot, Known, ProjectName, Segs } from '../ui.tsx'
import Detail, { asTab, type Tab } from './Detail.tsx'
import { PromptActions } from './Prompts.tsx'

export default function Run() {
  const params = useParams<{ run: string; item?: string }>()
  const [search] = useSearchParams<{ filter?: string; tab?: string; cancelled?: string }>()
  const filter = () => asFilter(search.filter)
  const tab = () => asTab(search.tab)
  const cancelled = () => search.cancelled === SHOW_CANCELLED

  const board = load(() => params.run, api.board)
  const selected = () => params.item ?? params.run
  const page = load(selected, api.item)

  const rows = () => new Map((board.data()?.rows ?? []).map((r) => [r.item.id, r]))

  /** Inside this run the board keeps its place; anything else goes through `/items/{id}`. */
  const href = (item: Pick<ItemRef, 'id' | 'kind'>, tabFor: Tab = tab(), cancelledFor = cancelled()) => {
    const to = boardHref(params.run, (id) => rows().has(id), item)
    const here = runHref(params.run)
    return to === here || to.startsWith(`${here}/`) ? `${to}${query(filter(), tabFor, cancelledFor)}` : to
  }

  const here = () => (params.item ? `${runHref(params.run)}/${params.item}` : runHref(params.run))

  /** Showing cancelled work in the plan means All: no other filter lists closed rows. */
  const planShown = () => cancelled() && filter() === 'all'
  const planToggle = () => `${here()}${planShown() ? query(filter(), tab(), false) : query('all', tab(), true)}`

  let detail: HTMLDivElement | undefined
  createEffect(
    on(
      () => params.item,
      (item, previous) => {
        if (item === previous || detail === undefined) return
        if (matchMedia('(max-width: 1100px)').matches) detail.scrollIntoView({ block: 'start' })
      },
      { defer: true },
    ),
  )

  return (
    <div class="page">
      <Show when={board.missing()}>
        <div class="panel empty">There is no run with this id. It may have been removed, or the link is from another workspace.</div>
      </Show>
      <Show when={board.error()}>{(e) => <div class="failure">{e()}</div>}</Show>

      <Show when={!board.missing()}>
        <Known value={board.data()} class="panel empty">
          {(b) => (
            <>
              <RunHeader board={b()} />
              <div class="work">
                <div class="panel plan">
                  <nav class="filters" aria-label="Filter the plan">
                    <For each={FILTERS}>
                      {([key, label]) => (
                        <A href={`${here()}${query(key, tab(), cancelled())}`} class={filter() === key ? 'on' : ''} noScroll>
                          {label}
                        </A>
                      )}
                    </For>
                    <CancelledToggle count={cancelledLines(b())} shown={planShown()} href={planToggle()} class="end" />
                  </nav>
                  <div class="plan-list">
                    <PlanList board={b()} filter={filter()} cancelled={cancelled()} selected={selected()} href={href} />
                  </div>
                </div>

                <div class="panel" ref={detail}>
                  <Show when={page.missing()}>
                    <div class="placeholder">There is no item with this id.</div>
                  </Show>
                  <Show when={page.error()}>{(e) => <div class="placeholder">{e()}</div>}</Show>
                  <Show when={!page.missing() && page.data()} fallback={<Show when={!page.missing()}><div class="placeholder">Loading…</div></Show>}>
                    {(p) => (
                      <Detail
                        page={p()}
                        run={b().run}
                        rows={rows()}
                        tab={tab()}
                        cancelled={cancelled()}
                        href={(item) => href(item)}
                        tabHref={(t) => href(p().item, t)}
                        cancelledHref={(show) => href(p().item, tab(), show)}
                      />
                    )}
                  </Show>
                </div>
              </div>
            </>
          )}
        </Known>
      </Show>
    </div>
  )
}

function RunHeader(props: { board: Board }) {
  const t = () => tally(props.board.rows)

  return (
    <div class="page-h">
      <div>
        <div class="crumbs">
          <A href="/">
            <Show when={props.board.project} fallback="Overview">
              {(project) => <ProjectName project={project()} />}
            </Show>
          </A>{' '}
          / {props.board.run.ref}
          <CopyRef item={props.board.run} />
        </div>
        <h1>{short(props.board.run.title)}</h1>
        <div class="sub">{progress(t())}</div>
        <PromptActions scope={props.board.run.id} />
      </div>
      <Segs leaves={t().leaves} style={{ width: 'min(420px, 40vw)' }} />
    </div>
  )
}

function PlanList(props: {
  board: Board
  filter: Filter
  cancelled: boolean
  selected: string
  href: (item: ItemRef) => string
}) {
  const groups = () => plan(props.board, props.filter, props.cancelled)

  return (
    <Index each={groups()} fallback={<div class="empty">Nothing matches this filter.</div>}>
      {(group) => (
        <>
          <Show
            when={group().head}
            fallback={
              <Show when={groups().length > 1}>
                <div class="group-h">Tasks</div>
              </Show>
            }
          >
            {(head) => (
              <A
                class={`group-h${props.selected === head().item.id ? ' on' : ''}`}
                href={props.href(head().item)}
                noScroll
              >
                <Dot k={rowState(head()).k} style={{ 'margin-top': 0 }} />
                {head().item.title}
                <span class="gc">
                  {group().tally?.done}/{group().tally?.total}
                </span>
              </A>
            )}
          </Show>
          <Index each={group().rows}>
            {(entry) => <PlanItem row={entry().row} indent={entry().indent} selected={props.selected} href={props.href} />}
          </Index>
        </>
      )}
    </Index>
  )
}

function PlanItem(props: { row: BoardRow; indent: number; selected: string; href: (item: ItemRef) => string }) {
  const s = () => rowState(props.row)
  const claim = () => (props.row.claim && !CLOSED.has(props.row.item.status) ? props.row.claim : null)
  const crit = () => (props.row.criteria.total > 0 ? `${props.row.criteria.checked}/${props.row.criteria.total}` : '')

  return (
    <A
      class={`item${props.selected === props.row.item.id ? ' on' : ''}${s().k === 'gone' ? ' gone' : ''}`}
      href={props.href(props.row.item)}
      style={{ 'padding-left': `${18 + Math.max(props.indent, 0) * 20}px` }}
      title={s().label}
      noScroll
      aria-current={props.selected === props.row.item.id ? 'page' : undefined}
    >
      <Dot k={s().k} />
      <div class="item-t">
        <span class="item-ref">{props.row.item.ref}</span>
        <CopyRef item={props.row.item} />
        {props.row.item.title}
      </div>
      <div class="item-side">
        <Show when={claim()}>{(c) => <span class="who">{claimHolder(c())}</span>}</Show>
        {crit()}
      </div>
    </A>
  )
}
