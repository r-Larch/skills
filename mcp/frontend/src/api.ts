import { createSignal } from 'solid-js'
import type { AppInfo, SessionTenant } from './connect.ts'

/** doc 10 — the frozen v1 shapes, as `WorkState.Api/App/V1/V1Contract.cs` serialises them. */
export type ItemRef = {
  id: string
  ref: string
  qualifiedRef: string
  kind: string
  title: string
  status: string
  /** `project/run/ref`: what every copy button copies. */
  fullRef: string
}

export type Rollup = {
  total: number
  done: number
  inProgress: number
  review: number
  blocked: number
  open: number
  cancelled: number
  superseded: number
}

export type Session = {
  id: string
  name: string
  role: string
  tree: string | null
  lastSeenAt: string
}

/** One row of `/api/v1/sessions`: the session in full, what it holds, and its token. */
export type SessionDetail = Session & {
  externalRole: string | null
  origin: string | null
  launchedBy: string | null
  claudeSessionId: string | null
  cwd: string | null
  client: string | null
  firstSeenAt: string
}

/** `none` is a session only ever named by header. */
export type TokenState = 'none' | 'live' | 'revoked' | 'expired'

export type SessionHolding = { session: SessionDetail; claims: Claim[]; token: TokenState }

export type Sessions = { asOf: string; seq: number; sessions: SessionHolding[] }

export type Released = { sessionId: string; name: string; releasedAs: string }

export type Claim = {
  item: ItemRef
  run: ItemRef | null
  session: Session | null
  agent: string
  role: string
  tree: string | null
  acquiredAt: string
  heartbeatAt: string
  expiresAt: string
  expired: boolean
  /** The holding session's name; null when the claim was taken without one. */
  heldBySession?: string | null
}

export type JournalEvent = {
  seq: number
  at: string
  item: ItemRef | null
  run: ItemRef | null
  session: Session | null
  actor: string
  role: string
  kind: string
  summary: string | null
}

export type Honesty = { doneUnverified: number; forced: number; ungated: number; followupsOpen: number }

export type RunOverview = {
  item: ItemRef
  rollup: Rollup
  branch: string | null
  worktree: string | null
  activeClaims: number
  lastEventAt: string | null
  inboxCount: number
  lastVerification: { passed: boolean; at: string } | null
}

export type ProjectOverview = {
  item: ItemRef
  rollup: Rollup
  runs: RunOverview[]
  archivedAt: string | null
  lastActivityAt: string | null
  openRuns: number
}

export type Overview = {
  asOf: string
  seq: number
  projects: ProjectOverview[]
  workingNow: Claim[]
  inboxCount: number
  honesty: Honesty
}

export type Verification = { passed: boolean; runBy: string; at: string }

export type BoardRow = {
  item: ItemRef
  parentId: string | null
  depth: number
  seq: string
  hasChildren: boolean
  resolution: string | null
  priority: number
  severity: string | null
  criteria: { checked: number; total: number }
  rollup: Rollup | null
  claim: Claim | null
  commit: string | null
  verification: Verification | null
  blockedBy: ItemRef[]
  flags: string[]
  openedAt: string
  startedAt: string | null
  closedAt: string | null
}

export type Board = {
  asOf: string
  seq: number
  run: ItemRef
  project: ItemRef | null
  intent: string | null
  branch: string | null
  worktree: string | null
  rollup: Rollup
  rows: BoardRow[]
}

export type Report = {
  attempt: number
  session: Session | null
  agent: string | null
  startedAt: string
  endedAt: string | null
  status: string
  files: string[]
  verify: string | null
  redCheck: string | null
  deviations: string | null
  risks: string | null
  notDone: string | null
  at: string
}

export type VerificationRecord = {
  command: string | null
  resultLine: string
  passed: boolean
  runBy: string
  session: Session | null
  redCheck: string | null
  at: string
}

export type Note = {
  id: string
  kind: string
  body: string
  from: ItemRef | null
  to: ItemRef | null
  consumedAt: string | null
  createdAt: string
  session: Session | null
}

export type ItemPage = {
  asOf: string
  seq: number
  item: ItemRef
  path: ItemRef[]
  intent: string | null
  verifyCmd: string | null
  riskTags: string[]
  priority: number
  severity: string | null
  source: string | null
  resolution: string | null
  closedBy: string | null
  openedAt: string
  startedAt: string | null
  closedAt: string | null
  criteria: { id: string; text: string; checked: boolean; evidence: string | null; checkedAt: string | null }[]
  claim: Claim | null
  reports: Report[]
  verifications: VerificationRecord[]
  artifacts: { kind: string; value: string; at: string }[]
  children: { item: ItemRef; criteria: { checked: number; total: number } }[]
  findings: { item: ItemRef; severity: string | null; source: string | null }[]
  notes: Note[]
  links: { out: { type: string; item: ItemRef }[]; in: { type: string; item: ItemRef }[] }
  assumptions: { ref: string; text: string; status: string; revisitWhen: ItemRef | null }[]
  events: JournalEvent[]
  eventsHasMore: boolean
  imported?: { path: string; anchor: string | null; why: string; importedBy: string; session: Session | null; at: string } | null
}

export type InboxRow = {
  key: string
  kind: string
  item: ItemRef
  run: ItemRef | null
  what: string
  since: string
  call: string
}

export type Inbox = { asOf: string; seq: number; rows: InboxRow[] }

export type JournalPage = { asOf: string; seq: number; cursor: number; hasMore: boolean; events: JournalEvent[] }

export type Backlog = {
  asOf: string
  seq: number
  run: ItemRef | null
  groups: { origin: ItemRef | null; items: ItemRef[] }[]
}

/** One prompt action (doc 13): the whole text a fresh session is started with. */
export type Prompt = { key: string; label: string; count: number | null; text: string }

export type Prompts = { asOf: string; seq: number; scope: ItemRef; prompts: Prompt[] }

/** `ref`: the query is the item's ref (resolve's rule); `prefix`: its ref starts with it; `text`: full text. */
export type SearchMatch = 'ref' | 'prefix' | 'text'

export type SearchHit = {
  item: ItemRef
  run: ItemRef | null
  project: ItemRef | null
  match: SearchMatch
  /** The matching fragment of the intent or title, plain text; only on a `text` hit. */
  snippet: string | null
}

export type Search = { asOf: string; seq: number; hits: SearchHit[] }

/**
 * Whether the cookie is good. Flipped by any 401, which is the only signal the API gives: the
 * session cookie is httponly, so the document cannot read it and asking is the only way to know.
 */
const [signedIn, setSignedIn] = createSignal(true)
export { signedIn }

export class Unauthorized extends Error {
  constructor() {
    super('not signed in')
  }
}

export class NotFound extends Error {
  constructor(path: string) {
    super(`${path} → 404`)
  }
}

async function get(path: string): Promise<Response> {
  const response = await fetch(path, { credentials: 'same-origin' })

  if (response.status === 401) {
    setSignedIn(false)
    throw new Unauthorized()
  }

  if (response.status === 404) throw new NotFound(path)
  if (!response.ok) throw new Error(`${path} → ${response.status}`)

  setSignedIn(true)
  return response
}

const json = async <T,>(path: string): Promise<T> => (await get(path)).json() as Promise<T>

const id = (value: string) => encodeURIComponent(value)

export const overview = () => json<Overview>('/api/v1/overview')

export const board = (run: string) => json<Board>(`/api/v1/runs/${id(run)}/board`)

export const item = (item: string) => json<ItemPage>(`/api/v1/items/${id(item)}`)

/** `text/plain`, and it stays text all the way to the `<pre>`. Never parsed, never re-encoded. */
export const brief = async (item: string): Promise<string> =>
  (await get(`/api/v1/items/${id(item)}/brief?for=resume`)).text()

export const inbox = () => json<Inbox>('/api/v1/inbox')

export const journal = (limit: number) => json<JournalPage>(`/api/v1/journal?limit=${limit}`)

export const backlog = (project: string) => json<Backlog>(`/api/v1/backlog?project=${id(project)}`)

export const prompts = (scope: string) => json<Prompts>(`/api/v1/prompts?scope=${id(scope)}`)

export const sessions = () => json<Sessions>('/api/v1/sessions')

export const search = (q: string) => json<Search>(`/api/v1/search?q=${id(q)}`)

/**
 * A dashboard write (doc 10). The header is the cross-site guard: a form on another site cannot
 * send it, and the server refuses the write without it. A refusal throws the server's own words.
 */
async function write(path: string): Promise<Response> {
  const response = await fetch(path, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'X-WorkState-Write': '1' },
  })

  if (response.status === 401) {
    setSignedIn(false)
    throw new Unauthorized()
  }

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: string } | null
    throw new Error(body?.error ?? `${path} → ${response.status}`)
  }

  return response
}

export async function setArchived(project: string, archived: boolean): Promise<void> {
  await write(`/api/projects/${id(project)}/${archived ? 'archive' : 'unarchive'}`)
}

/** Frees the session's name for the next mint; the row stays, renamed, for its history (doc 11). */
export const releaseSession = async (session: string): Promise<Released> =>
  (await write(`/api/sessions/${id(session)}/release`)).json() as Promise<Released>

/** `throttled` is the sign-in rate limit (429); `rejected` never says which field was wrong. */
export type SignInResult = 'ok' | 'rejected' | 'throttled'

export async function signIn(username: string, password: string): Promise<SignInResult> {
  const response = await fetch('/api/session', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })

  if (response.status === 204) {
    setSignedIn(true)
    return 'ok'
  }

  return response.status === 429 ? 'throttled' : 'rejected'
}

/** Whether the dashboard cookie is there, without touching the store. */
export async function hasSession(): Promise<boolean> {
  const response = await fetch('/api/session', { credentials: 'same-origin' })
  return response.status === 204
}

/** The workspaces the signed-in user may enter, the session's marked current; a status of 0 is no answer at all. */
export async function sessionTenants(): Promise<{ status: number; tenants: SessionTenant[] | null }> {
  try {
    const response = await fetch('/api/session/tenants', { credentials: 'same-origin' })
    return { status: response.status, tenants: response.ok ? ((await response.json()) as SessionTenant[]) : null }
  } catch {
    return { status: 0, tenants: null }
  }
}

/**
 * Moves the session to another workspace the user may enter. Null when it moved; otherwise the server's own
 * words. A dashboard write, so it carries the write header (doc 10).
 */
export async function switchTenant(id: string): Promise<string | null> {
  try {
    const response = await fetch('/api/session/tenant', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', 'X-WorkState-Write': '1' },
      body: JSON.stringify({ id }),
    })
    if (response.status === 204) return null
    const body = (await response.json().catch(() => null)) as { error?: string } | null
    return body?.error ?? `WorkState could not switch the workspace (${response.status}). Reload the page to try again.`
  } catch {
    return 'WorkState did not answer. Check that it is running, then reload this page.'
  }
}

/** The consent page's client; a status of 0 is no answer at all. */
export async function appInfo(search: string): Promise<{ status: number; info: AppInfo | null }> {
  const params = new URLSearchParams(search)
  const query = new URLSearchParams()
  for (const key of ['client_id', 'redirect_uri']) {
    const value = params.get(key)
    if (value !== null) query.set(key, value)
  }

  try {
    const response = await fetch(`/connect/app-info?${query}`, { credentials: 'same-origin' })
    return { status: response.status, info: response.ok ? ((await response.json()) as AppInfo) : null }
  } catch {
    return { status: 0, info: null }
  }
}
