import { For, Match, Show, Switch, createSignal, onMount } from 'solid-js'
import type { JSX } from 'solid-js'
import { appInfo, hasSession, sessionTenants, switchTenant } from './api.ts'
import {
  AUTHORIZE,
  SIGN_IN,
  TENANT_FIELD,
  type ConsentEvent,
  type ConsentState,
  type IconState,
  type SignInEvent,
  type SignInState,
  appIconHref,
  consentStep,
  consentView,
  hiddenFields,
  iconStep,
  initials,
  scopeLines,
  signInHref,
  signInStart,
  signInStep,
} from './connect.ts'
import Gate from './Gate.tsx'
import { CheckIcon, LinkIcon } from './icons.tsx'

/**
 * The OAuth authorize step's two pages, outside the dashboard's router and store: an MCP client sent the
 * browser here. The consent page names the workspace the token will act in, the session's, and nothing on
 * either page reads that workspace's board. Which page is the path's business.
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
  const [picked, setPicked] = createSignal<string | null>(null)
  const target = () => picked() ?? view().others[0]?.id ?? null

  onMount(async () => {
    const [{ status, info }, { status: tenantsStatus, tenants }] = await Promise.all([appInfo(search), sessionTenants()])
    if (status === 401 || tenantsStatus === 401) {
      location.replace(signInHref(search))
      return
    }
    step(info && tenants ? { t: 'loaded', info, tenants } : { t: 'failed', status: info ? tenantsStatus : status })
  })

  // The session moves, then the same consent URL loads again in the other tenant: authorize judges it there.
  const switchTo = async (id: string | null) => {
    if (id === null) return
    step({ t: 'switch', to: id })
    if (state().k !== 'switching') return
    const refused = await switchTenant(id)
    if (refused === null) location.reload()
    else step({ t: 'switch-failed', message: refused })
  }

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
          <Peers client={view().client} />

          <Show when={view().client} fallback={<p class="consent-title muted">Loading the client…</p>}>
            {(client) => (
              <p class="consent-title">
                <strong>{client()}</strong> wants to connect to your WorkState.
              </p>
            )}
          </Show>

          <Show when={view().client !== null}>
            <div class="consent-sec">
              <h2>Workspace</h2>
              <Show when={view().tenant} fallback={<p class="bad">WorkState could not say which workspace this connection is for. Deny, then start again.</p>}>
                {(tenant) => (
                  <p>
                    It acts in <strong class="wrap">{tenant().slug}</strong> and in no other workspace.
                  </p>
                )}
              </Show>
              <Show when={view().others.length > 0}>
                <div class="consent-switch">
                  <select aria-label="Another of your workspaces" disabled={!view().canAnswer} onChange={(event) => setPicked(event.currentTarget.value)}>
                    <For each={view().others}>{(other) => <option value={other.id}>{other.slug}</option>}</For>
                  </select>
                  <button type="button" class="secondary" disabled={!view().canAnswer} onClick={() => void switchTo(target())}>
                    {view().switchingTo === null ? 'Switch' : `Switching to ${view().switchingTo}…`}
                  </button>
                </div>
                <p class="consent-note">Each connection acts in one workspace. To connect it to another of yours, switch first.</p>
              </Show>
            </div>

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
          <Show when={view().tenant}>{(tenant) => <input type="hidden" name={TENANT_FIELD} value={tenant().id} />}</Show>

          <div class="consent-actions">
            <button type="submit" name="consent" value="denied" class="secondary" disabled={state().k === 'loading'}>
              {view().denyLabel}
            </button>
            <button type="submit" name="consent" value="granted" disabled={state().k === 'loading'}>
              {view().approveLabel}
            </button>
          </div>

          <p class="consent-note">WorkState remembers an approval for this client in this workspace, so its next connection here does not ask again.</p>
        </form>
      </div>
    </Show>
  )
}

/**
 * The client's tile, a connector, WorkState's mark. The image is in the page from the start, hidden, so it
 * loads alongside app-info; the tile stays a placeholder until both have answered.
 */
function Peers(props: { client: string | null }) {
  const href = appIconHref(location.search)
  const [icon, setIcon] = createSignal<IconState>(href === null ? 'initials' : 'loading')
  const shown = () => (props.client === null ? 'loading' : icon())

  return (
    <div class="peers" aria-hidden="true">
      <span class="peer" classList={{ 'peer-wait': shown() === 'loading' }}>
        <Show when={href}>
          {(src) => (
            <img
              src={src()}
              alt=""
              hidden={shown() !== 'image'}
              onLoad={() => setIcon(iconStep(icon(), 'load'))}
              onError={() => setIcon(iconStep(icon(), 'error'))}
            />
          )}
        </Show>
        <Show when={shown() === 'initials'}>
          <span class="peer-initials">{initials(props.client ?? '')}</span>
        </Show>
      </span>
      <span class="peer-link">
        <i />
        <LinkIcon />
        <i />
      </span>
      <span class="peer peer-self">
        <CheckIcon size={24} weight={2.6} />
      </span>
    </div>
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
