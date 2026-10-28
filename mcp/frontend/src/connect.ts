/**
 * The two pages inside `/connect/authorize` — sign-in and consent — as plain functions, so
 * `verify/state-cases.mjs` can walk every state without a browser. `Connect.tsx` only renders these.
 * The server judges the same things first (`WorkState.Api/Auth/OAuth/ConnectPages.cs`); these are the
 * page's own copy of the rules, not a substitute for them.
 */

export const AUTHORIZE = '/connect/authorize'
export const SIGN_IN = '/connect/signin'
export const CONSENT = '/connect/consent'

/** The pages this module owns; every other path is the dashboard. */
export const isConnectPath = (pathname: string): boolean => pathname === SIGN_IN || pathname === CONSENT

/**
 * Where the sign-in page may continue to: this server's authorize endpoint and nothing else, so a link
 * to the sign-in page cannot be made to forward a fresh session anywhere. Null refuses it.
 */
export function returnTarget(raw: string | null): string | null {
  if (raw === null) return null
  return raw === AUTHORIZE || raw.startsWith(`${AUTHORIZE}?`) ? raw : null
}

/** The consent page's query without the answer field: what goes back to authorize as hidden inputs. */
export function hiddenFields(search: string): [string, string][] {
  return [...new URLSearchParams(search).entries()].filter(([key]) => key !== 'consent')
}

/** Back through sign-in, to the authorize request this consent page was opened for. */
export function signInHref(search: string): string {
  const query = new URLSearchParams(hiddenFields(search)).toString()
  return `${SIGN_IN}?returnUrl=${encodeURIComponent(query ? `${AUTHORIZE}?${query}` : AUTHORIZE)}`
}

/** `GET /connect/app-info`, as `ConnectPages.AppInfo` serialises it. */
export type AppInfo = {
  clientName: string
  /** The host serving the client's metadata document; null for a client that registered itself. */
  clientHost: string | null
  /** Null unless the request's callback is one the client registered. */
  redirectHost: string | null
  loopbackOnly: boolean
}

/**
 * `GET /connect/app-icon`: the client's own icon, resolved and served by this server
 * (`WorkState.Api/Auth/OAuth/ClientIcons.cs`), so the page asks no third party about the client and its
 * CSP allows images from this origin only. Null without a client id.
 */
export function appIconHref(search: string): string | null {
  const clientId = new URLSearchParams(search).get('client_id')
  return clientId ? `/connect/app-icon?client_id=${encodeURIComponent(clientId)}` : null
}

/** What the client's tile shows: a placeholder until the image answers, then the image or the initials. */
export type IconState = 'loading' | 'image' | 'initials'

export const iconStep = (state: IconState, event: 'load' | 'error'): IconState =>
  state === 'loading' ? (event === 'load' ? 'image' : 'initials') : state

/** Two words give their first letters, one word its first two; never empty. */
export function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean).map((word) => [...word])
  const letters = words.length >= 2 ? words.slice(0, 2).map((word) => word[0]) : (words[0] ?? []).slice(0, 2)
  return letters.join('').toUpperCase() || '?'
}

export type Answer = 'granted' | 'denied'

export type ConsentState =
  | { k: 'loading' }
  | { k: 'shown'; info: AppInfo }
  | { k: 'answering'; info: AppInfo; answer: Answer }
  | { k: 'error'; message: string }

export type ConsentEvent =
  | { t: 'loaded'; info: AppInfo }
  | { t: 'failed'; status: number }
  | { t: 'answer'; answer: Answer }

/** A 401 is not an error state: the page goes back through sign-in instead (`signInHref`). */
export function failure(status: number): string {
  if (status === 400) return 'This page was opened without a client. Start the connection again from your MCP client.'
  if (status === 404) return 'No client by that id is registered here. Start the connection again from your MCP client.'
  if (status === 0) return 'WorkState did not answer. Check that it is running, then reload this page.'
  return `WorkState could not describe this client (${status}). Reload the page to try again.`
}

export function consentStep(state: ConsentState, event: ConsentEvent): ConsentState {
  switch (event.t) {
    case 'loaded':
      return state.k === 'loading' ? { k: 'shown', info: event.info } : state
    case 'failed':
      return state.k === 'loading' ? { k: 'error', message: failure(event.status) } : state
    case 'answer':
      // One answer per page: a second click while the first is in flight changes nothing.
      return state.k === 'shown' ? { k: 'answering', info: state.info, answer: event.answer } : state
  }
}

export type ScopeLine = { scope: string; label: string; detail: string }

const SCOPES: Record<string, Omit<ScopeLine, 'scope'> | null> = {
  mcp: {
    label: 'Full access to your WorkState as controller',
    detail: 'Everything a controller token can do: every project, run and task, to read and to write.',
  },
  'workstate.read': {
    label: 'Read your board and its live updates',
    detail: 'Every project, run and task as the dashboard shows them, and the live stream of changes. Nothing is written.',
  },
  'workstate.sessions': {
    label: 'Start agent sessions on your behalf',
    detail: 'Create, rename and end agent session tokens, up to controller. Signing this app out ends the sessions it started.',
  },
  offline_access: {
    label: 'Stay connected',
    detail: 'Keep access without asking again until you revoke it or it goes unused for 30 days.',
  },
  openid: null,
  profile: null,
}

/** What the client gets, from the request's own `scope`; identity scopes say nothing worth a line. */
export function scopeLines(search: string): ScopeLine[] {
  const requested = (new URLSearchParams(search).get('scope') ?? '').split(' ').filter(Boolean)
  const lines: ScopeLine[] = []
  for (const scope of new Set(requested)) {
    const known = SCOPES[scope]
    if (known === null) continue
    lines.push({ scope, ...(known ?? { label: scope, detail: '' }) })
  }
  return lines
}

export type ConsentView = {
  /** The client's name, or null while there is none to show. */
  client: string | null
  /** Where the answer goes; null with a client shown means the callback is not one it registered. */
  destination: string | null
  /** Who vouches for the name: the metadata document's host, or the client itself. */
  declaredBy: string | null
  loopbackOnly: boolean
  canAnswer: boolean
  approveLabel: string
  denyLabel: string
  error: string | null
}

export function consentView(state: ConsentState): ConsentView {
  const info = state.k === 'shown' || state.k === 'answering' ? state.info : null
  const answering = state.k === 'answering' ? state.answer : null
  return {
    client: info?.clientName ?? null,
    destination: info?.redirectHost ?? null,
    declaredBy: info?.clientHost ?? null,
    loopbackOnly: info?.loopbackOnly ?? false,
    canAnswer: state.k === 'shown',
    approveLabel: answering === 'granted' ? 'Approving…' : 'Approve',
    denyLabel: answering === 'denied' ? 'Denying…' : 'Deny',
    error: state.k === 'error' ? state.message : null,
  }
}

/** The form an answer submits: the hidden fields, then the pressed button's own name and value. */
export const consentForm = (search: string, answer: Answer): [string, string][] => [...hiddenFields(search), ['consent', answer]]

export type SignInState = { k: 'invalid' } | { k: 'checking'; target: string } | { k: 'form'; target: string } | { k: 'continuing'; target: string }

export type SignInEvent = { t: 'session'; present: boolean } | { t: 'signed-in' }

/**
 * Before the form, the page asks `GET /api/session` whether a session is already there. That same-site
 * request carries the SameSite=Strict cookie a cross-site arrival did not, and continuing from here is a
 * same-site navigation that carries it to authorize.
 */
export function signInStart(search: string): SignInState {
  const target = returnTarget(new URLSearchParams(search).get('returnUrl'))
  return target === null ? { k: 'invalid' } : { k: 'checking', target }
}

export function signInStep(state: SignInState, event: SignInEvent): SignInState {
  if (state.k === 'invalid' || state.k === 'continuing') return state
  if (event.t === 'signed-in') return { k: 'continuing', target: state.target }
  if (state.k !== 'checking') return state
  return event.present ? { k: 'continuing', target: state.target } : { k: 'form', target: state.target }
}
