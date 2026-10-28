import { For, Show, createSignal } from 'solid-js'
import { A, useParams } from '@solidjs/router'
import * as api from '../api.ts'
import type { AdminMember, CreatedInvite, PendingInvite } from '../api.ts'
import { account, load, refreshAccount } from '../store.ts'
import { ADMIN } from '../nav.ts'
import { ago, day } from '../format.ts'
import { ROLES, expiresIn, isLastAdmin, roleLabel } from '../admin.ts'
import { Dot, Known, OnceLink } from '../ui.tsx'
import { NotAdmin, ResetLink } from './Admin.tsx'

const message = (failure: unknown) => (failure instanceof Error ? failure.message : String(failure))

/** `/admin/tenants/{id}` (doc 13): one workspace's members and its invite links. */
export default function AdminTenant() {
  const params = useParams()
  const [tick, setTick] = createSignal(0)
  const key = () => `${params.tenant}:${tick()}`
  const tenants = load(() => 'tenants', () => api.adminTenants(), { live: false })
  const members = load(key, () => api.adminMembers(params.tenant!), { live: false })
  const invites = load(key, () => api.adminInvites(params.tenant!), { live: false })
  const tenant = () => tenants.data()?.find((t) => t.id === params.tenant)
  const reload = () => setTick((n) => n + 1)

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <div class="sub">
            <A href={ADMIN}>Admin</A> · Workspace
          </div>
          <h1>{tenant()?.slug ?? '—'}</h1>
          <Show when={tenant()?.current}>
            <div class="sub">The workspace you are in.</div>
          </Show>
        </div>
      </div>

      <NotAdmin />
      <Show when={members.error() ?? invites.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
      <Show when={members.missing()}>
        <div class="panel empty">
          There is no workspace with this id. <A href={ADMIN}>Back to Admin</A>.
        </div>
      </Show>

      <Show when={!members.missing()}>
        <div class="grid-2">
          <Known value={members.data()} class="panel empty">
            {(list) => (
              <div class="panel">
                <div class="panel-h">
                  <h2>Members</h2>
                  <span class="n">{list().length}</span>
                </div>
                <For each={list()} fallback={<div class="empty">No members yet. Create an invite link to add the first.</div>}>
                  {(m) => <MemberRow tenant={params.tenant!} member={m} members={list()} onChanged={reload} />}
                </For>
              </div>
            )}
          </Known>

          <Invites tenant={params.tenant!} pending={invites.data()} onChanged={reload} />
        </div>
      </Show>
    </div>
  )
}

function MemberRow(props: { tenant: string; member: AdminMember; members: AdminMember[]; onChanged: () => void }) {
  const [confirming, setConfirming] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()
  const m = () => props.member
  const self = () => account()?.user.id === m().userId
  const locked = () => isLastAdmin(props.members, m().userId)

  const act = async (write: () => Promise<void>) => {
    setBusy(true)
    setError(undefined)
    try {
      await write()
      setConfirming(false)
      // A change to one's own membership can change what the sidebar offers.
      if (self()) void refreshAccount()
      props.onChanged()
    } catch (failure) {
      setError(message(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="row admin-row">
      <Dot k={m().disabledAt ? 'gone' : m().role === 'admin' ? 'doing' : 'idle'} />
      <div>
        <div class="row-t">
          {m().name}
          {self() ? ' (you)' : ''}
        </div>
        <div class="row-m">
          {roleLabel(m().role)}
          {m().systemAdmin ? ' · system administrator' : ''} · joined {day(m().joinedAt)}
          {m().disabledAt ? ` · disabled ${day(m().disabledAt!)}` : ''}
        </div>
        <ResetLink user={m().userId} name={m().name} />
        <Show when={confirming()}>
          <div class="confirm" role="alertdialog" aria-label={`Remove ${m().name}?`}>
            <p>
              Remove <strong>{m().name}</strong> from this workspace? Their session here ends at its next request, and
              tokens they hold for this workspace stop working. Their account and their work stay; an invite brings them
              back.
            </p>
            <div class="confirm-b">
              <button type="button" class="proj-act danger" disabled={busy()} onClick={() => void act(() => api.removeMember(props.tenant, m().userId))}>
                Remove
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
        <select
          aria-label={`Role of ${m().name}`}
          value={m().role}
          disabled={busy() || locked()}
          title={locked() ? 'The last admin: make another member an admin first' : undefined}
          onChange={(e) => {
            const role = e.currentTarget.value
            void act(() => api.setMemberRole(props.tenant, m().userId, role))
          }}
        >
          <For each={ROLES}>{(role) => <option value={role}>{roleLabel(role)}</option>}</For>
        </select>
        <button
          type="button"
          class="proj-act"
          disabled={busy() || confirming() || locked()}
          title={locked() ? 'The last admin: make another member an admin first' : undefined}
          onClick={() => setConfirming(true)}
        >
          Remove
        </button>
      </div>
    </div>
  )
}

/** Create a link (shown once), and the ones not used yet, each with Revoke. */
function Invites(props: { tenant: string; pending: PendingInvite[] | undefined; onChanged: () => void }) {
  const [role, setRole] = createSignal<string>('member')
  const [created, setCreated] = createSignal<CreatedInvite>()
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()

  const create = async () => {
    setBusy(true)
    setError(undefined)
    try {
      setCreated(await api.createInvite(props.tenant, role()))
      props.onChanged()
    } catch (failure) {
      setError(message(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="panel">
      <div class="panel-h">
        <h2>Invite links</h2>
        <span class="n">{props.pending ? `${props.pending.length} pending` : ''}</span>
      </div>
      <div class="admin-form">
        <select aria-label="Role the invite grants" value={role()} onChange={(e) => setRole(e.currentTarget.value)}>
          <For each={ROLES}>{(r) => <option value={r}>{roleLabel(r)}</option>}</For>
        </select>
        <button type="button" class="proj-act primary" disabled={busy()} onClick={() => void create()}>
          {busy() ? 'Creating…' : 'Create invite link'}
        </button>
        <p class="admin-hint">
          No email is sent: copy the link to the person. It works once, for 14 days, and makes a new account or adds a
          signed-in one.
        </p>
        <Show when={error()}>{(text) => <div class="failure confirm-err">{text()}</div>}</Show>
      </div>
      <Show when={created()}>
        {(invite) => (
          <OnceLink url={invite().url} onDismiss={() => setCreated(undefined)}>
            {roleLabel(invite().role)} invite to <strong>{invite().tenant}</strong>, until {day(invite().expiresAt)}. Copy it
            now: it is shown once, and the server keeps only a hash of it.
          </OnceLink>
        )}
      </Show>
      <Known value={props.pending}>
        {(list) => (
          <For each={list()} fallback={<div class="empty">No invite is waiting to be used.</div>}>
            {(invite) => <InviteRow tenant={props.tenant} invite={invite} onChanged={props.onChanged} />}
          </For>
        )}
      </Known>
    </div>
  )
}

function InviteRow(props: { tenant: string; invite: PendingInvite; onChanged: () => void }) {
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()
  const now = new Date().toISOString()

  const revoke = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await api.revokeInvite(props.tenant, props.invite.id)
      props.onChanged()
    } catch (failure) {
      setError(message(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="row admin-row">
      <Dot k="wait" />
      <div>
        <div class="row-t">{roleLabel(props.invite.role)} invite</div>
        <div class="row-m">
          by {props.invite.createdBy ?? 'a removed user'} · {ago(props.invite.createdAt, now)} · expires{' '}
          {expiresIn(props.invite.expiresAt, Date.now())}
        </div>
        <Show when={error()}>{(text) => <div class="failure confirm-err">{text()}</div>}</Show>
      </div>
      <div class="admin-acts">
        <button type="button" class="proj-act" disabled={busy()} onClick={() => void revoke()} title="The link stops working at once">
          Revoke
        </button>
      </div>
    </div>
  )
}

