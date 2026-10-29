import { Match, Show, Switch, createSignal, onMount } from 'solid-js'
import { resetInfo, useReset } from './api.ts'
import { type ResetEvent, type ResetState, PASSWORD_MIN, passwordProblem, resetStep, resetToken } from './admin.ts'
import { clock, day } from './format.ts'
import { CheckIcon } from './icons.tsx'

/**
 * `/reset/{token}`, the page a reset link opens: outside the dashboard like the invite page, and in front of no
 * sign-in gate. It sets the named user's password once; every session of that user ends, this browser's
 * included, so it ends by offering the sign-in.
 */
export default function Reset() {
  const token = resetToken(location.pathname) ?? ''
  const [state, setState] = createSignal<ResetState>({ k: 'loading' })
  const step = (event: ResetEvent) => setState(resetStep(state(), event))
  const [password, setPassword] = createSignal('')
  const [repeat, setRepeat] = createSignal('')
  const [tried, setTried] = createSignal(false)

  onMount(async () => {
    const { status, body, error } = await resetInfo(token)
    step(body ? { t: 'loaded', reset: body } : { t: 'failed', status, error })
  })

  const problem = () => passwordProblem(password()) ?? (password() === repeat() ? null : 'The two passwords differ.')

  const submit = async (event: SubmitEvent) => {
    event.preventDefault()
    setTried(true)
    if (problem() !== null) return
    step({ t: 'save' })
    if (state().k !== 'saving') return
    const { status, body, error } = await useReset(token, password())
    step(body ? { t: 'saved' } : { t: 'refused', status, error })
  }

  const shown = () => {
    const s = state()
    return s.k === 'shown' || s.k === 'saving' ? s : null
  }
  const invalid = () => {
    const s = state()
    return s.k === 'invalid' ? s.message : null
  }
  const done = () => {
    const s = state()
    return s.k === 'done' ? s.name : null
  }
  const refusal = () => {
    const s = state()
    return s.k === 'shown' ? s.error : null
  }

  return (
    <div class="gate-wrap">
      <div class="panel gate consent">
        <div class="brand">
          <span class="brand-mark">
            <CheckIcon size={16} weight={2.6} />
          </span>
          <span class="brand-name">WorkState</span>
        </div>
        <Switch>
          <Match when={state().k === 'loading'}>
            <p class="muted">Reading the reset link…</p>
          </Match>
          <Match when={invalid()}>
            {(text) => (
              <>
                <p class="consent-title">{text()}</p>
                <p class="consent-note">
                  <a href="/">Open WorkState</a>
                </p>
              </>
            )}
          </Match>
          <Match when={done()}>
            {(name) => (
              <>
                <p class="consent-title">
                  The password of <strong>{name()}</strong> is set. Every session of {name()} was signed out.
                </p>
                <a class="join-go" href="/">
                  Sign in with it
                </a>
              </>
            )}
          </Match>
          <Match when={shown()}>
            {(s) => (
              <form onSubmit={(event) => void submit(event)}>
                <p class="consent-title">
                  Set a new password for <strong>{s().reset.name}</strong>.
                </p>
                <p class="consent-note">
                  This link works once, until {day(s().reset.expiresAt)} {clock(s().reset.expiresAt)}. Setting the password
                  signs {s().reset.name} out everywhere.
                </p>
                <label for="reset-password">New password</label>
                <input
                  id="reset-password"
                  type="password"
                  autocomplete="new-password"
                  value={password()}
                  onInput={(e) => setPassword(e.currentTarget.value)}
                />
                <label for="reset-repeat">New password, again</label>
                <input
                  id="reset-repeat"
                  type="password"
                  autocomplete="new-password"
                  value={repeat()}
                  onInput={(e) => setRepeat(e.currentTarget.value)}
                />
                <p class="consent-note">At least {PASSWORD_MIN} characters.</p>
                <button type="submit" disabled={s().k === 'saving'}>
                  {s().k === 'saving' ? 'Setting…' : 'Set the password'}
                </button>
                <Show when={tried() && problem()}>{(text) => <p class="bad">{text()}</p>}</Show>
                <Show when={refusal()}>{(text) => <p class="bad">{text()}</p>}</Show>
              </form>
            )}
          </Match>
        </Switch>
      </div>
    </div>
  )
}
