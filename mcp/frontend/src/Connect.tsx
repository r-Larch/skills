import { For, Match, Show, Switch, createSignal, onMount } from 'solid-js'
import type { JSX } from 'solid-js'
import { appInfo, hasSession } from './api.ts'
import {
  AUTHORIZE,
  SIGN_IN,
  type ConsentEvent,
  type ConsentState,
  type SignInEvent,
  type SignInState,
  consentStep,
  consentView,
  hiddenFields,
  scopeLines,
  signInHref,
  signInStart,
  signInStep,
} from './connect.ts'
import Gate from './Gate.tsx'
import { CheckIcon } from './icons.tsx'

/**
 * The OAuth authorize step's two pages, outside the dashboard's router and store: an MCP client sent the
 * browser here, and nothing on these pages reads the workspace. Which one is the path's business.
 */
export default function Connect() {
  return (
    <Show when={location.pathname === SIGN_IN} fallback={<Consent />}>
      <SignIn />
    </Show>
  )
}

function SignIn() {
  const [state, setState] = createSignal<SignInState>(signInStart(location.search))

  const step = (event: SignInEvent) => {
    const next = signInStep(state(), event)
    setState(next)
    if (next.k === 'continuing') location.replace(next.target)
  }

  onMount(() => {
    if (state().k === 'checking') void hasSession().then((present) => step({ t: 'session', present }))
  })

  return (
    <Switch>
      <Match when={state().k === 'invalid'}>
        <Notice>This sign-in link does not lead back to an authorization request. Start the connection again from your MCP client.</Notice>
      </Match>
      <Match when={state().k === 'form'}>
        <Gate intro="Sign in to connect an MCP client to this WorkState." onSignedIn={() => step({ t: 'signed-in' })} />
      </Match>
      <Match when={true}>
        <Notice quiet>Continuing…</Notice>
      </Match>
    </Switch>
  )
}

function Consent() {
  const search = location.search
  const [state, setState] = createSignal<ConsentState>({ k: 'loading' })
  const step = (event: ConsentEvent) => setState(consentStep(state(), event))
  const view = () => consentView(state())
  const scopes = scopeLines(search)

  onMount(async () => {
    const { status, info } = await appInfo(search)
    if (status === 401) {
      location.replace(signInHref(search))
      return
    }
    step(info ? { t: 'loaded', info } : { t: 'failed', status })
  })

  return (
    <Show when={view().error === null} fallback={<Notice>{view().error}</Notice>}>
      <div class="gate-wrap">
        <form
          class="panel gate consent"
          method="post"
          action={AUTHORIZE}
          aria-busy={state().k === 'loading'}
          onSubmit={(event) => {
            // The pressed button carries the answer as its own name and value, so it must stay enabled
            // while the form submits — a disabled submitter is not sent. The state ignores a second press.
            const answer = (event.submitter as HTMLButtonElement | null)?.value
            if (!view().canAnswer || (answer !== 'granted' && answer !== 'denied')) {
              event.preventDefault()
              return
            }
            step({ t: 'answer', answer })
          }}
        >
          <Brand />

          <Show when={view().client} fallback={<p class="consent-title muted">Loading the client…</p>}>
            {(client) => (
              <p class="consent-title">
                <strong>{client()}</strong> wants to connect to your WorkState.
              </p>
            )}
          </Show>

          <Show when={view().client !== null}>
            <div class="consent-sec">
              <h2>Destination</h2>
              <Show
                when={view().destination}
                fallback={<p class="bad">The address this request returns to is not one the client registered. Deny unless you know why.</p>}
              >
                {(host) => (
                  <p>
                    Your answer goes back to <strong class="wrap">{host()}</strong>.
                  </p>
                )}
              </Show>
              <p class="consent-note">
                <Show when={view().declaredBy} fallback={<>The name is the one the client registered itself under; WorkState cannot vouch for it.</>}>
                  {(host) => (
                    <>
                      The name is published by <strong class="wrap">{host()}</strong>.
                    </>
                  )}
                </Show>
              </p>
              <Show when={view().loopbackOnly}>
                <p class="consent-warn">Every address this client returns to is on this computer: it is a program running here. Approve only if you just started it.</p>
              </Show>
            </div>

            <Show when={scopes.length > 0}>
              <div class="consent-sec">
                <h2>It gets</h2>
                <For each={scopes}>
                  {(line) => (
                    <div class="consent-scope">
                      <strong>{line.label}</strong>
                      <Show when={line.detail}>
                        <span>{line.detail}</span>
                      </Show>
                    </div>
                  )}
                </For>
              </div>
            </Show>
          </Show>

          <For each={hiddenFields(search)}>{([name, value]) => <input type="hidden" name={name} value={value} />}</For>

          <div class="consent-actions">
            <button type="submit" name="consent" value="denied" class="secondary" disabled={state().k === 'loading'}>
              {view().denyLabel}
            </button>
            <button type="submit" name="consent" value="granted" disabled={state().k === 'loading'}>
              {view().approveLabel}
            </button>
          </div>

          <p class="consent-note">WorkState remembers an approval for this client, so its next connection does not ask again.</p>
        </form>
      </div>
    </Show>
  )
}

function Brand() {
  return (
    <div class="brand">
      <span class="brand-mark">
        <CheckIcon size={16} weight={2.6} />
      </span>
      <span class="brand-name">WorkState</span>
    </div>
  )
}

function Notice(props: { children: JSX.Element; quiet?: boolean }) {
  return (
    <div class="gate-wrap">
      <div class="panel gate">
        <Brand />
        <p classList={{ bad: !props.quiet, muted: props.quiet }}>{props.children}</p>
      </div>
    </div>
  )
}
