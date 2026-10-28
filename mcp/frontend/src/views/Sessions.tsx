import { For, Show, createSignal } from 'solid-js'
import * as api from '../api.ts'
import type { SessionHolding } from '../api.ts'
import { load } from '../store.ts'
import { ago, plural } from '../format.ts'
import { formerName, isReleased, releasable, tokenLabel } from '../sessions.ts'
import { Dot, Known } from '../ui.tsx'

/** doc 11's sessions, most recently seen first, each with the operator's Release. */
export default function Sessions() {
  const [tick, setTick] = createSignal(0)
  const page = load(() => String(tick()), () => api.sessions())

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Sessions</h1>
          <div class="sub">
            Every agent session the store knows, most recently seen first. Release frees a session's name so the next
            launch under it starts fresh.
          </div>
        </div>
      </div>

      <Show when={page.error()}>{(e) => <div class="failure">{e()}</div>}</Show>

      <Known value={page.data()} class="panel empty">
        {(p) => (
          <div class="panel">
            <For each={p().sessions} fallback={<div class="empty">No session has called yet.</div>}>
              {(row) => <SessionRow row={row} asOf={p().asOf} onReleased={() => setTick((n) => n + 1)} />}
            </For>
          </div>
        )}
      </Known>
    </div>
  )
}

function SessionRow(props: { row: SessionHolding; asOf: string; onReleased: () => void }) {
  const s = () => props.row.session
  const held = () => props.row.claims.filter((c) => !c.expired).length

  return (
    <div class="row">
      <Dot k={isReleased(s().name) ? 'gone' : props.row.token === 'live' || held() > 0 ? 'doing' : 'idle'} />
      <div>
        <div class="row-t">
          {isReleased(s().name) ? `${formerName(s().name)} (released)` : s().name}
        </div>
        <div class="row-m">
          {s().role} · launched by {s().launchedBy ?? 'no launcher'} · seen {ago(s().lastSeenAt, props.asOf)} ·{' '}
          {tokenLabel(props.row.token)}
          {held() > 0 ? ` · holds ${held()} ${plural(held(), 'claim')}` : ''}
          {s().claudeSessionId ? '' : ' · no Claude session recorded'}
        </div>
        <Show when={releasable(props.row)}>
          <ReleaseControl name={s().name} id={s().id} onReleased={props.onReleased} />
        </Show>
      </div>
      <span />
    </div>
  )
}

/** Asks first and says what it does, as archiving does; a refusal is shown in the server's words. */
function ReleaseControl(props: { name: string; id: string; onReleased: () => void }) {
  const [confirming, setConfirming] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()

  const release = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await api.releaseSession(props.id)
      setConfirming(false)
      props.onReleased()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div style={{ 'margin-top': '6px' }}>
      <button type="button" class="proj-act" disabled={busy() || confirming()} onClick={() => setConfirming(true)}>
        Release
      </button>
      <Show when={confirming()}>
        <div class="confirm" role="alertdialog" aria-label={`Release ${props.name}?`} style={{ 'margin-top': '8px' }}>
          <p>
            Release <strong>{props.name}</strong>? Its token stops working and the next launch under this name starts a
            new session. Nothing is deleted: its claims, evidence and journal stay with the old row, kept under a
            released name. A session that still holds claims is refused.
          </p>
          <div class="confirm-b">
            <button type="button" class="proj-act danger" disabled={busy()} onClick={() => void release()}>
              Release
            </button>
            <button type="button" class="proj-act" disabled={busy()} onClick={() => setConfirming(false)}>
              Cancel
            </button>
          </div>
        </div>
      </Show>
      <Show when={error()}>{(message) => <div class="failure confirm-err">Couldn't release it: {message()}</div>}</Show>
    </div>
  )
}
