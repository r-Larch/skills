import { For, Show, createSignal } from 'solid-js'
import * as api from '../api.ts'
import type { ProjectMember, ProjectMembers } from '../api.ts'
import { refreshOverview } from '../store.ts'
import { day, plural } from '../format.ts'
import { MEMBERS_ANCHOR, memberLine } from '../sharing.ts'
import { Dot } from '../ui.tsx'

const message = (failure: unknown) => (failure instanceof Error ? failure.message : String(failure))

/**
 * A project's members (doc 10, `/api/projects/{id}/members`): who is in it, Add from the workspace's other users,
 * Add everyone while any remain, Remove per member. A write's answer replaces the list at once through `onWritten`;
 * the journal's member_added / member_removed frame re-reads it after.
 */
export function MembersPanel(props: {
  view: ProjectMembers
  me: string | undefined
  onWritten: (view: ProjectMembers) => void
  /** After the caller removed themselves: they can no longer see the project. */
  onLeft: () => void
}) {
  const [picked, setPicked] = createSignal('')
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()
  const candidates = () => props.view.candidates
  const choice = () => (candidates().some((c) => c.userId === picked()) ? picked() : (candidates()[0]?.userId ?? ''))

  const add = async (who: string | 'all') => {
    setBusy(true)
    setError(undefined)
    try {
      props.onWritten(await api.addProjectMember(props.view.projectId, who))
      setPicked('')
      void refreshOverview()
    } catch (failure) {
      setError(message(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="panel members">
      <div class="panel-h">
        <h2 id={MEMBERS_ANCHOR} tabindex="-1">
          Members
        </h2>
        <span class="n">{props.view.members.length}</span>
      </div>
      <For each={props.view.members} fallback={<div class="empty">No one has joined yet, so everyone in the workspace can see it.</div>}>
        {(member) => (
          <MemberRow
            project={props.view.projectId}
            member={member}
            self={member.userId === props.me}
            onWritten={props.onWritten}
            onLeft={props.onLeft}
          />
        )}
      </For>
      <Show
        when={candidates().length > 0}
        fallback={
          <Show when={props.view.members.length > 0}>
            <p class="members-hint">Everyone in the workspace is a member.</p>
          </Show>
        }
      >
        <div class="admin-form">
          <select aria-label="Person to add" value={choice()} disabled={busy()} onChange={(e) => setPicked(e.currentTarget.value)}>
            <For each={candidates()}>
              {(c) => (
                <option value={c.userId}>
                  {c.name}
                  {c.disabledAt ? ' (disabled)' : ''}
                </option>
              )}
            </For>
          </select>
          <button type="button" class="proj-act primary" disabled={busy() || choice() === ''} onClick={() => void add(choice())}>
            Add member
          </button>
          <button type="button" class="proj-act" disabled={busy()} onClick={() => void add('all')}>
            Add everyone
          </button>
          <p class="admin-hint">
            Add everyone adds the {candidates().length} {plural(candidates().length, 'person', 'people')} in the workspace who
            {candidates().length === 1 ? " isn't" : " aren't"} a member yet. Members see the project's work; no one else does.
          </p>
          <Show when={error()}>{(text) => <div class="failure confirm-err">{text()}</div>}</Show>
        </div>
      </Show>
    </div>
  )
}

function MemberRow(props: {
  project: string
  member: ProjectMember
  self: boolean
  onWritten: (view: ProjectMembers) => void
  onLeft: () => void
}) {
  const [confirming, setConfirming] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()
  const m = () => props.member

  const remove = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await api.removeProjectMember(props.project, m().userId)
      setConfirming(false)
      if (props.self) {
        void refreshOverview()
        props.onLeft()
        return
      }
      props.onWritten(await api.projectMembers(props.project))
      void refreshOverview()
    } catch (failure) {
      setError(message(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="row admin-row">
      <Dot k={m().disabledAt ? 'gone' : 'idle'} />
      <div>
        <div class="row-t">
          {m().name}
          {props.self ? ' (you)' : ''}
        </div>
        <div class="row-m">{memberLine(m(), day(m().addedAt))}</div>
        <Show when={confirming()}>
          <div class="confirm" role="alertdialog" aria-label="Remove yourself?">
            <p>
              Remove yourself from this project? You'll lose access to it: its work leaves your overview and your agents can
              no longer read it. Another member can add you back.
            </p>
            <div class="confirm-b">
              <button type="button" class="proj-act danger" disabled={busy()} onClick={() => void remove()}>
                Remove me
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
        <button
          type="button"
          class="proj-act"
          aria-label={`Remove ${m().name}`}
          disabled={busy() || confirming()}
          onClick={() => (props.self ? setConfirming(true) : void remove())}
        >
          Remove
        </button>
      </div>
    </div>
  )
}
