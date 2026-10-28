import { For, Show, createSignal } from 'solid-js'
import { A, useNavigate } from '@solidjs/router'
import * as api from '../api.ts'
import { account, load, refreshAccount } from '../store.ts'
import { USERS, tenantAdminHref } from '../nav.ts'
import { plural } from '../format.ts'
import { slugFrom, slugProblem } from '../admin.ts'
import { Dot, Known } from '../ui.tsx'

/** `/admin` (doc 13): the workspaces the user administers, and for a system administrator a new one. */
export default function Admin() {
  const tenants = load(() => 'tenants', () => api.adminTenants(), { live: false })
  const systemAdmin = () => account()?.user.systemAdmin ?? false

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Admin</h1>
          <div class="sub">
            The workspaces you administer, their members and their invite links. Every member has full work rights; an
            admin also manages the workspace's members and invites.
          </div>
        </div>
        <Show when={systemAdmin()}>
          <A href={USERS} class="proj-act">
            All users
          </A>
        </Show>
      </div>

      <NotAdmin />
      <Show when={tenants.error()}>{(e) => <div class="failure">{e()}</div>}</Show>

      <div class="stack">
        <Known value={tenants.data()} class="panel empty">
          {(list) => (
            <div class="panel">
              <div class="panel-h">
                <h2>Workspaces</h2>
                <span class="n">{list().length}</span>
              </div>
              <For each={list()} fallback={<div class="empty">No workspace to administer.</div>}>
                {(t) => (
                  <A class="row" href={tenantAdminHref(t.id)}>
                    <Dot k={t.current ? 'doing' : 'idle'} title={t.current ? 'The workspace you are in' : undefined} />
                    <div>
                      <div class="row-t mono">{t.slug}</div>
                      <div class="row-m">
                        {t.members} {plural(t.members, 'member')} · {t.admins} {plural(t.admins, 'admin')} ·{' '}
                        {t.pendingInvites} pending {plural(t.pendingInvites, 'invite')}
                        {t.current ? ' · the workspace you are in' : ''}
                        {t.admins === 0 ? ' · no admin yet: invite one' : ''}
                      </div>
                    </div>
                    <span class="row-go">Manage</span>
                  </A>
                )}
              </For>
            </div>
          )}
        </Known>

        <Show when={systemAdmin()}>
          <NewTenant />
        </Show>
      </div>
    </div>
  )
}

/** A page of the admin area opened by someone it is not for: the server refuses every read, this says why. */
export function NotAdmin() {
  return (
    <Show when={account() && !account()!.user.administers}>
      <div class="failure">
        The admin pages are for a workspace's admins and for system administrators, and {account()!.user.name} is
        neither. A workspace admin can make you one.
      </div>
    </Show>
  )
}

/** A new workspace, by slug; it opens on its own page, where the first invite makes its first admin. */
function NewTenant() {
  const navigate = useNavigate()
  const [slug, setSlug] = createSignal('')
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()
  const problem = () => (slug() === '' ? null : slugProblem(slug()))

  const submit = async (event: SubmitEvent) => {
    event.preventDefault()
    const wanted = slugFrom(slug())
    setSlug(wanted)
    if (slugProblem(wanted)) return
    setBusy(true)
    setError(undefined)
    try {
      const created = await api.createTenant(wanted)
      void refreshAccount()
      navigate(tenantAdminHref(created.id))
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form class="panel" onSubmit={(event) => void submit(event)}>
      <div class="panel-h">
        <h2>New workspace</h2>
      </div>
      <div class="admin-form">
        <input
          aria-label="Workspace slug"
          placeholder="team-two"
          autocomplete="off"
          spellcheck={false}
          value={slug()}
          onInput={(e) => setSlug(e.currentTarget.value)}
          onBlur={() => setSlug(slugFrom(slug()))}
        />
        <button type="submit" class="proj-act primary" disabled={busy() || slug() === '' || problem() !== null}>
          {busy() ? 'Creating…' : 'Create workspace'}
        </button>
        <p class="admin-hint">
          {problem() ?? 'Lowercase letters and digits, words joined by hyphens. It has no members yet: invite its first admin from its page.'}
        </p>
        <Show when={error()}>{(message) => <div class="failure confirm-err">{message()}</div>}</Show>
      </div>
    </form>
  )
}
