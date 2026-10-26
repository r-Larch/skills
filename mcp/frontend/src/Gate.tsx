import { Show, createSignal } from 'solid-js'
import { signIn } from './api.ts'
import { CheckIcon } from './icons.tsx'

/**
 * Not a view — a gate. `/api/*` is cookie-guarded and the cookie is httponly, so the only way the
 * SPA learns it has no session is a 401, and the only way out is a form. No credential is carried
 * in this source: the fields start empty and stay empty.
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
    <div class="gate-wrap">
      <form class="panel gate" onSubmit={(event) => void submit(event)}>
        <div class="brand">
          <span class="brand-mark">
            <CheckIcon size={16} weight={2.6} />
          </span>
          <span class="brand-name">WorkState</span>
        </div>

        <label for="u">Username</label>
        <input id="u" autocomplete="username" value={username()} onInput={(event) => setUsername(event.currentTarget.value)} />

        <label for="p">Password</label>
        <input
          id="p"
          type="password"
          autocomplete="current-password"
          value={password()}
          onInput={(event) => setPassword(event.currentTarget.value)}
        />

        <button type="submit" disabled={busy()}>
          {busy() ? 'Checking…' : 'Sign in'}
        </button>

        <Show when={rejected()}>
          <p class="bad">Rejected. The dashboard credential is configured server-side.</p>
        </Show>
      </form>
    </div>
  )
}
