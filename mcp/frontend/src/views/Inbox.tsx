import { For, Index } from 'solid-js'
import { A } from '@solidjs/router'
import type { InboxRow } from '../api.ts'
import { inbox } from '../store.ts'
import { groupInbox, inboxKind } from '../inbox.ts'
import { itemHref } from '../nav.ts'
import { ago } from '../format.ts'
import { CopyRef, Dot, Known } from '../ui.tsx'

export function InboxRowView(props: { row: InboxRow; asOf: string }) {
  const r = () => props.row
  const kind = () => inboxKind(r().kind)

  return (
    <A class="row" href={itemHref(r().item, r().run)}>
      <Dot k={kind().dot} />
      <div>
        <div class="row-t">{r().item.title}</div>
        <div class="row-m">
          {kind().label} · {r().item.qualifiedRef} <CopyRef value={r().item.qualifiedRef} />· {r().what} ·{' '}
          {ago(r().since, props.asOf)}
        </div>
        <div class="row-call mono" title={r().call}>
          {r().call}
        </div>
      </div>
      <span />
    </A>
  )
}

export default function Inbox() {
  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Needs you</h1>
          <div class="sub">Everything waiting on a person, newest first. Each row shows the call that resolves it.</div>
        </div>
      </div>

      <Known value={inbox()} class="panel empty">
        {(box) => (
          <>
            <div class="summary">
              <For each={groupInbox(box().rows)}>
                {(group) => (
                  <span class="chip">
                    {group.label} <b>{group.rows.length}</b>
                  </span>
                )}
              </For>
            </div>
            <div class="panel">
              <Index each={box().rows} fallback={<div class="empty">Nothing is waiting on you.</div>}>
                {(row) => <InboxRowView row={row()} asOf={box().asOf} />}
              </Index>
            </div>
          </>
        )}
      </Known>
    </div>
  )
}
