import { Show, createSignal } from 'solid-js'
import { signIn } from './api'

/**
 * Not a third view — a gate. `/api/*` is cookie-guarded and the cookie is httponly, so the only way
 * the SPA learns it has no session is a 401, and the only way out is a form. No credential is
 * carried in this source: the fields start empty and stay empty.
 */
export default function Gate() {
  const [username, setUsername] = createSignal('')
  const [password, setPassword] = createSignal('')
  const [rejected, setRejected] = createSignal(false)
  const [busy, setBusy] = createSignal(false)

  const submit = async (event: SubmitEvent) => {
    event.preventDefault()
    setBusy(true)
    setRejected(!(await signIn(username(), password())))
    setBusy(false)
  }

  return (
    <div class="wrap">
      <form class="gate" onSubmit={(event) => void submit(event)}>
        <div class="brand">
          workstate <span>/ sign in</span>
        </div>

        <label for="u">Username</label>
        <input
          id="u"
          autocomplete="username"
          value={username()}
          onInput={(event) => setUsername(event.currentTarget.value)}
        />

        <label for="p">Password</label>
        <input
          id="p"
          type="password"
          autocomplete="current-password"
          value={password()}
          onInput={(event) => setPassword(event.currentTarget.value)}
        />

        <button type="submit" disabled={busy()}>
          {busy() ? 'checking…' : 'sign in'}
        </button>

        <Show when={rejected()}>
          <p class="bad">Rejected. The dashboard credential is configured server-side.</p>
        </Show>
      </form>
    </div>
  )
}
