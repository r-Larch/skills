import { Match, Show, Switch, createSignal, onMount } from 'solid-js'
import { acceptInvite, inviteInfo, signOut } from './api.ts'
import { type InviteView, type JoinEvent, type JoinState, PASSWORD_MIN, joinStep, joinToken, nameProblem, passwordProblem, roleLabel } from './admin.ts'
import { day } from './format.ts'
import Gate from './Gate.tsx'
import { CheckIcon } from './icons.tsx'

/**
 * `/join/{token}`, the page an invite link opens — outside the dashboard's router, store and stream, and in
 * front of no sign-in gate: the token is the credential. A signed-in user accepts as themselves; anyone else
 * creates an account, or signs in first and then accepts. Accepting signs in to the invite's workspace.
 */
export default function Join() {
  const token = joinToken(location.pathname) ?? ''
  const [state, setState] = createSignal<JoinState>({ k: 'loading' })
  const step = (event: JoinEvent) => setState(joinStep(state(), event))

  onMount(async () => {
    const { status, body, error } = await inviteInfo(token)
    step(body ? { t: 'loaded', invite: body } : { t: 'failed', status, error })
  })

  const accept = async (account: { name: string; password: string } | null) => {
    step({ t: 'accept' })
    if (state().k !== 'accepting') return
    const { status, body, error } = await acceptInvite(token, account)
    if (body) {
      step({ t: 'accepted' })
      location.replace('/')
    } else step({ t: 'refused', status, error })
  }

  const shown = () => {
    const s = state()
    return s.k === 'shown' || s.k === 'accepting' ? s : null
  }
  const invalid = () => {
    const s = state()
    return s.k === 'invalid' ? s.message : null
  }
  const refusal = () => {
    const s = state()
    return s.k === 'shown' ? s.error : null
  }

  return (
    <Switch>
      <Match when={shown()?.mode === 'sign-in' && !shown()!.invite.signedInAs}>
        <Gate intro={`Sign in, then join ${shown()!.invite.tenant} as yourself.`} onSignedIn={() => location.reload()} />
      </Match>
      <Match when={true}>
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
                <p class="muted">Reading the invite…</p>
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
              <Match when={state().k === 'joined'}>
                <p class="muted">Joined. Opening the dashboard…</p>
              </Match>
              <Match when={shown()}>
                {(s) => (
                  <Invite
                    invite={s().invite}
                    busy={s().k === 'accepting'}
                    error={refusal()}
                    onAccept={(a) => void accept(a)}
                    onSignIn={() => step({ t: 'mode', mode: 'sign-in' })}
                  />
                )}
              </Match>
            </Switch>
          </div>
        </div>
      </Match>
    </Switch>
  )
}

function Invite(props: {
  invite: InviteView
  busy: boolean
  error: string | null
  onAccept: (account: { name: string; password: string } | null) => void
  onSignIn: () => void
}) {
  const [name, setName] = createSignal('')
  const [password, setPassword] = createSignal('')
  const [repeat, setRepeat] = createSignal('')
  const [tried, setTried] = createSignal(false)

  const problem = () =>
    nameProblem(name()) ?? passwordProblem(password()) ?? (password() === repeat() ? null : 'The two passwords differ.')

  const submit = (event: SubmitEvent) => {
    event.preventDefault()
    setTried(true)
    if (problem() === null) props.onAccept({ name: name().trim(), password: password() })
  }

  const leave = async () => {
    await signOut()
    location.reload()
  }

  return (
    <>
      <p class="consent-title">
        Join <strong>{props.invite.tenant}</strong> as {roleLabel(props.invite.role).toLowerCase()}.
      </p>
      <p class="consent-note">This link works once, until {day(props.invite.expiresAt)}.</p>

      <Show
        when={props.invite.signedInAs}
        fallback={
          <form onSubmit={submit}>
            <label for="join-name">Name</label>
            <input id="join-name" autocomplete="username" value={name()} onInput={(e) => setName(e.currentTarget.value)} />
            <label for="join-password">Password</label>
            <input
              id="join-password"
              type="password"
              autocomplete="new-password"
              value={password()}
              onInput={(e) => setPassword(e.currentTarget.value)}
            />
            <label for="join-repeat">Password, again</label>
            <input
              id="join-repeat"
              type="password"
              autocomplete="new-password"
              value={repeat()}
              onInput={(e) => setRepeat(e.currentTarget.value)}
            />
            <p class="consent-note">At least {PASSWORD_MIN} characters. You sign in with this name and password.</p>
            <button type="submit" disabled={props.busy}>
              {props.busy ? 'Joining…' : 'Create account and join'}
            </button>
            <Show when={tried() && problem()}>{(text) => <p class="bad">{text()}</p>}</Show>
            <p class="consent-note">
              Already have an account?{' '}
              <button type="button" class="linkish" onClick={() => props.onSignIn()}>
                Sign in first
              </button>
            </p>
          </form>
        }
      >
        {(who) => (
          <>
            <div class="consent-sec">
              <p>
                You are signed in as <strong>{who()}</strong>.
                {props.invite.member
                  ? ` You are already a member of ${props.invite.tenant}; joining keeps your role${props.invite.role === 'admin' ? ' and makes you an admin' : ''}.`
                  : ''}
              </p>
            </div>
            <button type="button" class="join-go" disabled={props.busy} onClick={() => props.onAccept(null)}>
              {props.busy ? 'Joining…' : `Join as ${who()}`}
            </button>
            <p class="consent-note">
              Not you?{' '}
              <button type="button" class="linkish" disabled={props.busy} onClick={() => void leave()}>
                Sign out
              </button>
            </p>
          </>
        )}
      </Show>

      <Show when={props.error}>{(text) => <p class="bad">{text()}</p>}</Show>
    </>
  )
}
