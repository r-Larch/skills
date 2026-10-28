import { For, Show, createSignal } from 'solid-js'
import { A } from '@solidjs/router'
import * as api from '../api.ts'
import type { AdminUser } from '../api.ts'
import { account, load } from '../store.ts'
import { ADMIN, tenantAdminHref } from '../nav.ts'
import { day } from '../format.ts'
import { roleLabel } from '../admin.ts'
import { Dot, Known } from '../ui.tsx'

const message = (failure: unknown) => (failure instanceof Error ? failure.message : String(failure))

/** `/admin/users` (doc 13): every user, for a system administrator, with Disable / Enable. */
export default function AdminUsers() {
  const [tick, setTick] = createSignal(0)
  const users = load(() => String(tick()), () => api.adminUsers(), { live: false })

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <div class="sub">
            <A href={ADMIN}>Admin</A> · Users
          </div>
          <h1>Users</h1>
          <div class="sub">
            Everyone who can sign in to this WorkState, and the workspaces each is a member of. Disabling signs a user out
            everywhere at their next request and stops their OAuth connections and agent sessions; nothing is deleted.
          </div>
        </div>
      </div>

      <Show when={account() && !account()!.user.systemAdmin}>
        <div class="failure">Only a system administrator lists every user.</div>
      </Show>
      <Show when={users.error()}>{(e) => <div class="failure">{e()}</div>}</Show>

      <Known value={users.data()} class="panel empty">
        {(list) => (
          <div class="panel">
            <div class="panel-h">
              <h2>Users</h2>
              <span class="n">{list().length}</span>
            </div>
            <For each={list()}>{(user) => <UserRow user={user} onChanged={() => setTick((n) => n + 1)} />}</For>
          </div>
        )}
      </Known>
    </div>
  )
}

function UserRow(props: { user: AdminUser; onChanged: () => void }) {
  const [confirming, setConfirming] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()
  const u = () => props.user
  const self = () => account()?.user.id === u().id

  const set = async (disabled: boolean) => {
    setBusy(true)
    setError(undefined)
    try {
      await api.setUserDisabled(u().id, disabled)
      setConfirming(false)
      props.onChanged()
    } catch (failure) {
      setError(message(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="row admin-row">
      <Dot k={u().disabledAt ? 'gone' : u().systemAdmin ? 'doing' : 'idle'} />
      <div>
        <div class="row-t">
          {u().name}
          {self() ? ' (you)' : ''}
        </div>
        <div class="row-m">
          {u().systemAdmin ? 'System administrator · ' : ''}since {day(u().createdAt)}
          {u().disabledAt ? ` · disabled ${day(u().disabledAt!)}` : ''}
        </div>
        <div class="chips admin-chips">
          <For each={u().tenants} fallback={<span class="chip">a member of no workspace</span>}>
            {(t) => (
              <A class="chip" href={tenantAdminHref(t.id)}>
                <b>{t.slug}</b> {roleLabel(t.role)}
              </A>
            )}
          </For>
        </div>
        <Show when={confirming()}>
          <div class="confirm" role="alertdialog" aria-label={`Disable ${u().name}?`}>
            <p>
              Disable <strong>{u().name}</strong>? They cannot sign in, their open sessions end at their next request, and
              their OAuth connections and the agent sessions they started stop working. Their memberships and work stay;
              Enable undoes it.
            </p>
            <div class="confirm-b">
              <button type="button" class="proj-act danger" disabled={busy()} onClick={() => void set(true)}>
                Disable
              </button>
              <button type="button" class="proj-act" disabled={busy()} onClick={() => setConfirming(false)}>
                Cancel
              </button>
            </div>
          </div>
        </Show>
        <Show when={error()}>{(text) => <div class="failure confirm-err">{text()}</div>}</Show>
      </div>
      <div class="admin-acts">
        <Show
          when={u().disabledAt}
          fallback={
            <Show when={!self()}>
              <button type="button" class="proj-act" disabled={busy() || confirming()} onClick={() => setConfirming(true)}>
                Disable
              </button>
            </Show>
          }
        >
          <button type="button" class="proj-act" disabled={busy()} onClick={() => void set(false)}>
            Enable
          </button>
        </Show>
      </div>
    </div>
  )
}
