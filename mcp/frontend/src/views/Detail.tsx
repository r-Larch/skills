import { For, Match, Show, Switch, type JSX } from 'solid-js'
import { A } from '@solidjs/router'
import * as api from '../api.ts'
import type { BoardRow, ItemPage, ItemRef } from '../api.ts'
import { load } from '../store.ts'
import { CLOSED, pageInput, refState, rowState, state } from '../state.ts'
import { ago, cap, clock, day } from '../format.ts'
import { CheckIcon } from '../icons.tsx'
import { Dot, Pill, ProjectName } from '../ui.tsx'
import { EventLine } from './Activity.tsx'

export type Tab = 'overview' | 'evidence' | 'notes' | 'activity' | 'brief'

export const asTab = (value: string | undefined): Tab =>
  value === 'evidence' || value === 'notes' || value === 'activity' || value === 'brief' ? value : 'overview'

type Props = {
  page: ItemPage
  run: ItemRef | null
  rows: Map<string, BoardRow>
  tab: Tab
  /** The href for another item, carrying whatever query the page keeps (filter, tab). */
  href: (item: ItemRef) => string
  /** The href for this item on another tab. */
  tabHref: (tab: Tab) => string
}

export default function Detail(props: Props) {
  const p = () => props.page
  const s = () => state(pageInput(p()))
  const open = () => !CLOSED.has(p().item.status)
  const notes = () => [...p().notes].sort((a, b) => b.createdAt.localeCompare(a.createdAt))

  const commit = () =>
    [...p().artifacts].filter((a) => a.kind === 'commit').sort((a, b) => b.at.localeCompare(a.at))[0]?.value

  const chips = (): [string, string][] => {
    const c = p().claim
    const list: ([string, string] | false | null | undefined | '')[] = [
      ['kind', p().item.kind],
      commit() !== undefined && ['commit', commit()!],
      c && open() && ['claimed by', c.session?.name ?? c.agent],
      c?.tree && open() && ['tree', c.tree],
      p().priority !== 0 && ['priority', String(p().priority)],
      p().severity && ['severity', p().severity!],
      p().resolution && ['resolution', p().resolution!],
      p().closedAt && ['closed', `${day(p().closedAt!)} ${clock(p().closedAt!)}`],
      !p().closedAt && p().startedAt && ['started', ago(p().startedAt, p().asOf)],
    ]
    return list.filter((x): x is [string, string] => Array.isArray(x))
  }

  const tabs = (): [Tab, string, number | null][] => [
    ['overview', 'Overview', null],
    ['evidence', 'Evidence', p().reports.length + p().verifications.length],
    ['notes', 'Notes', p().notes.length],
    ['activity', 'Activity', p().events.length],
    ['brief', 'Brief', null],
  ]

  return (
    <>
      <div class="detail-head">
        <div class="crumbs">
          <For each={p().path}>
            {(step, i) => (
              <>
                {i() > 0 ? ' / ' : ''}
                <A href={props.href(step)}>
                  <Show when={step.kind === 'project'} fallback={step.ref}>
                    <ProjectName project={step} />
                  </Show>
                </A>
              </>
            )}
          </For>
        </div>
        <div class="dh-row">
          <div>
            <h2 class="dh-title">{p().item.title}</h2>
            <div class="dh-ref">{p().item.qualifiedRef}</div>
          </div>
          <Pill state={s()} />
        </div>
        <div class="chips">
          <For each={chips()}>
            {([k, v]) => (
              <span class="chip" title={v}>
                {k} <b>{v}</b>
              </span>
            )}
          </For>
        </div>
      </div>

      <nav class="tabs" aria-label="Item sections">
        <For each={tabs()}>
          {([key, label, n]) => (
            <A href={props.tabHref(key)} class={props.tab === key ? 'on' : ''} noScroll aria-current={props.tab === key ? 'page' : undefined}>
              {label}
              <Show when={n}>
                <span class="tn">{n}</span>
              </Show>
            </A>
          )}
        </For>
      </nav>

      <div class="tab">
        <Switch>
          <Match when={props.tab === 'overview'}>
            <OverviewTab {...props} />
          </Match>
          <Match when={props.tab === 'evidence'}>
            <EvidenceTab page={p()} />
          </Match>
          <Match when={props.tab === 'notes'}>
            <For each={notes()} fallback={<div class="empty bare">No notes.</div>}>
              {(n) => (
                <div class="note">
                  <div class={`note-k k-${n.kind}`}>
                    {cap(n.kind.replace(/_/g, ' '))}
                    <span>
                      {day(n.createdAt)} {clock(n.createdAt)}
                    </span>
                    <span>
                      {n.to?.id === p().item.id ? (n.consumedAt ? 'delivered' : 'not yet read') : n.to ? `to ${n.to.qualifiedRef}` : 'record'}
                    </span>
                  </div>
                  <div class="note-b">{n.body}</div>
                </div>
              )}
            </For>
          </Match>
          <Match when={props.tab === 'activity'}>
            <For each={[...p().events].reverse()} fallback={<div class="empty bare">No events recorded.</div>}>
              {(event) => <EventLine event={event} withRef={event.item?.id !== p().item.id} />}
            </For>
            <Show when={p().eventsHasMore}>
              <p class="more">
                The newest {p().events.length} are shown. The rest are in <A href="/activity">Activity</A>.
              </p>
            </Show>
          </Match>
          <Match when={props.tab === 'brief'}>
            <BriefTab id={p().item.id} />
          </Match>
        </Switch>
      </div>
    </>
  )
}

function Section(props: { title: string; children: JSX.Element }) {
  return (
    <>
      <div class="sec-h">{props.title}</div>
      {props.children}
    </>
  )
}

function OverviewTab(props: Props) {
  const p = () => props.page
  const blockers = () => p().links.in.filter((l) => l.type === 'blocks' && !CLOSED.has(l.item.status)).map((l) => l.item)
  const checked = () => p().criteria.filter((c) => c.checked).length
  const childState = (item: ItemRef) => {
    const row = props.rows.get(item.id)
    return row ? rowState(row) : refState(item)
  }
  const empty = () =>
    !p().intent && !p().imported && blockers().length === 0 && p().criteria.length === 0 && !p().verifyCmd && p().children.length === 0 && p().findings.length === 0

  return (
    <>
      <Show when={p().intent}>
        {(intent) => (
          <Section title="What this is for">
            <div class="prose">{intent()}</div>
          </Section>
        )}
      </Show>

      <Show when={p().imported}>
        {(imported) => (
          <Section title="Imported from">
            <div class="prose">
              <code>{imported().anchor ? `${imported().path}#${imported().anchor}` : imported().path}</code> by{' '}
              {imported().session?.name ?? imported().importedBy}, {day(imported().at)}
            </div>
            <div class="prose">{imported().why}</div>
          </Section>
        )}
      </Show>

      <Show when={blockers().length > 0}>
        <Section title="Waiting on">
          <div class="kids">
            <For each={blockers()}>
              {(b) => (
                <A class="row" href={props.href(b)}>
                  <Dot k={childState(b).k} />
                  <div>
                    <span class="item-ref">{b.qualifiedRef}</span>
                    {b.title}
                  </div>
                  <span class="row-m">{childState(b).label}</span>
                </A>
              )}
            </For>
          </div>
        </Section>
      </Show>

      <Show when={p().criteria.length > 0}>
        <Section title={`Done when · ${checked()} of ${p().criteria.length}`}>
          <For each={p().criteria}>
            {(c) => (
              <div class="crit">
                <span class={`box${c.checked ? ' on' : ''}`}>{c.checked ? <CheckIcon /> : null}</span>
                <div>
                  <div>{c.text}</div>
                  <Show when={c.evidence}>{(e) => <div class="crit-ev">{e()}</div>}</Show>
                </div>
              </div>
            )}
          </For>
        </Section>
      </Show>

      <Show when={p().verifyCmd}>
        {(cmd) => (
          <Section title="Verified by running">
            <div class="ev-cmd mono">{cmd()}</div>
          </Section>
        )}
      </Show>

      <Show when={p().findings.length > 0}>
        <Section title="Findings against this">
          <div class="kids">
            <For each={p().findings}>
              {(f) => (
                <A class="row" href={props.href(f.item)}>
                  <Dot k={childState(f.item).k} />
                  <div>
                    <span class="item-ref">{f.item.qualifiedRef}</span>
                    {f.item.title}
                  </div>
                  <span class="row-m">{f.severity ?? ''}</span>
                </A>
              )}
            </For>
          </div>
        </Section>
      </Show>

      <Show when={p().children.length > 0}>
        <Section title="Inside">
          <div class="kids">
            <For each={p().children}>
              {(child) => (
                <A class="row" href={props.href(child.item)}>
                  <Dot k={childState(child.item).k} />
                  <div>
                    <span class="item-ref">{child.item.ref}</span>
                    {child.item.title}
                  </div>
                  <span class="row-m">{childState(child.item).label}</span>
                </A>
              )}
            </For>
          </div>
        </Section>
      </Show>

      <Show when={empty()}>
        <div class="empty bare">No description, criteria or children recorded.</div>
      </Show>
    </>
  )
}

function EvidenceTab(props: { page: ItemPage }) {
  const reports = () => [...props.page.reports].sort((a, b) => b.at.localeCompare(a.at))
  const verifications = () => [...props.page.verifications].sort((a, b) => b.at.localeCompare(a.at))

  return (
    <div class="evidence">
      <div class="ev-col">
        <h3>
          <Dot k="claimed" style={{ margin: 0 }} />
          What the agent reported
        </h3>
        <p class="hint">From work_report. A claim, not proof.</p>
        <For each={reports()} fallback={<div class="empty bare">No reports.</div>}>
          {(r) => {
            const fields = (): [string, string][] =>
              (
                [
                  ['files', r.files.join('\n')],
                  ['verify', r.verify],
                  ['red-check', r.redCheck],
                  ['deviations', r.deviations],
                  ['risks', r.risks],
                  ['not done', r.notDone],
                ] as [string, string | null][]
              ).filter((x): x is [string, string] => !!x[1])
            return (
              <div class="ev">
                <div class="ev-top">
                  <span>
                    {r.session?.name ?? r.agent ?? 'agent'} · attempt {r.attempt}
                  </span>
                  <span>
                    {day(r.at)} {clock(r.at)}
                  </span>
                </div>
                <div class="ev-line">Reported {r.status}</div>
                <Show when={fields().length > 0}>
                  <dl>
                    <For each={fields()}>
                      {([k, v]) => (
                        <>
                          <dt>{k}</dt>
                          <dd>{v}</dd>
                        </>
                      )}
                    </For>
                  </dl>
                </Show>
              </div>
            )
          }}
        </For>
      </div>

      <div class="ev-col">
        <h3>
          <Dot k="proven" style={{ margin: 0 }} />
          What was verified
        </h3>
        <p class="hint">From work_verify, run by someone other than the author.</p>
        <For each={verifications()} fallback={<div class="empty bare">Nothing verified yet.</div>}>
          {(v) => (
            <div class="ev">
              <div class="ev-top">
                <span class={`pill ${v.passed ? 'p-proven' : 'p-stop'}`}>{v.passed ? 'Passed' : 'Failed'}</span>
                <span>
                  run by {v.runBy}
                  {v.session ? ` (${v.session.name})` : ''} · {day(v.at)} {clock(v.at)}
                </span>
              </div>
              <div class="ev-line">{v.resultLine}</div>
              <Show when={v.command}>{(cmd) => <div class="ev-cmd mono">{cmd()}</div>}</Show>
              <Show when={v.redCheck}>
                {(red) => (
                  <dl>
                    <dt>red-check</dt>
                    <dd>{red()}</dd>
                  </dl>
                )}
              </Show>
            </div>
          )}
        </For>
      </div>
    </div>
  )
}

function BriefTab(props: { id: string }) {
  const text = load(() => props.id, api.brief, { live: false })

  return (
    <>
      <p class="hint" style={{ margin: '0 0 12px', color: 'var(--ink-3)', 'font-size': '12.5px' }}>
        The verbatim work_brief an agent resuming this item would read.
      </p>
      <Show when={text.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
      <Show when={text.data() !== undefined} fallback={<div class="empty bare">Loading…</div>}>
        <pre class="brief-text">{text.data()}</pre>
      </Show>
    </>
  )
}
