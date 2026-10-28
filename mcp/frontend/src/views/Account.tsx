import { Show, createSignal } from 'solid-js'
import * as api from '../api.ts'
import { account } from '../store.ts'
import { PASSWORD_MIN, type PasswordChange, passwordMessage, passwordProblem } from '../admin.ts'

/** `/account` (doc 13): who is signed in, and the password change. */
export default function Account() {
  const [current, setCurrent] = createSignal('')
  const [next, setNext] = createSignal('')
  const [repeat, setRepeat] = createSignal('')
  const [tried, setTried] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [result, setResult] = createSignal<PasswordChange>()

  const problem = () =>
    current() === '' ? 'Type your current password.' : (passwordProblem(next()) ?? (next() === repeat() ? null : 'The two new passwords differ.'))

  const submit = async (event: SubmitEvent) => {
    event.preventDefault()
    setTried(true)
    setResult(undefined)
    if (problem() !== null) return
    setBusy(true)
    try {
      const outcome = await api.changePassword(current(), next())
      setResult(outcome)
      if (outcome.k === 'ok') {
        setCurrent('')
        setNext('')
        setRepeat('')
        setTried(false)
      }
    } catch (failure) {
      if (!(failure instanceof api.Unauthorized)) {
        setResult({ k: 'refused', error: failure instanceof Error ? failure.message : String(failure) })
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Account</h1>
          <div class="sub">
            <Show when={account()} fallback="Signed in.">
              {(a) => (
                <>
                  Signed in as <strong>{a().user.name}</strong>
                  {a().user.systemAdmin ? ', a system administrator' : ''}
                  {a().tenants.find((t) => t.current) ? `, in ${a().tenants.find((t) => t.current)!.slug}` : ''}.
                </>
              )}
            </Show>
          </div>
        </div>
      </div>

      <form class="panel account-form" onSubmit={(event) => void submit(event)}>
        <div class="panel-h">
          <h2>Change password</h2>
        </div>
        <div class="admin-form">
          <label for="pw-current">Current password</label>
          <input id="pw-current" type="password" autocomplete="current-password" value={current()} onInput={(e) => setCurrent(e.currentTarget.value)} />
          <label for="pw-next">New password</label>
          <input id="pw-next" type="password" autocomplete="new-password" value={next()} onInput={(e) => setNext(e.currentTarget.value)} />
          <label for="pw-repeat">New password, again</label>
          <input id="pw-repeat" type="password" autocomplete="new-password" value={repeat()} onInput={(e) => setRepeat(e.currentTarget.value)} />
          <p class="admin-hint">
            At least {PASSWORD_MIN} characters. Changing it signs out your other sessions; this one stays, and so do the
            apps you connected with OAuth.
          </p>
          <button type="submit" class="proj-act primary" disabled={busy()}>
            {busy() ? 'Changing…' : 'Change password'}
          </button>
          <Show when={tried() && problem()}>{(text) => <div class="failure confirm-err">{text()}</div>}</Show>
          <Show when={result()}>
            {(r) => <div class={r().k === 'ok' ? 'done-note' : 'failure confirm-err'}>{passwordMessage(r())}</div>}
          </Show>
        </div>
      </form>
    </div>
  )
}
