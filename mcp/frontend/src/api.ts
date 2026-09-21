import { createSignal } from 'solid-js'

/** The shapes `WorkState.Api/App/AppContract.cs` serialises. Names are camelCased by the host. */
export type Project = { ref: string; title: string; status: string; runCount: number }

export type Lease = {
  agent: string
  ref: string
  qualifiedRef: string
  title: string
  expiresAt: string
}

export type Attention = {
  rank: string
  ref: string | null
  qualifiedRef: string | null
  what: string
  call: string
}

export type Honesty = {
  doneUnverified: number
  forced: number
  ungated: number
  followupsOpen: number
}

export type Watch = {
  project: string | null
  asOf: string
  workingNow: Lease[]
  needsYou: Attention[]
  honesty: Honesty
}

export type TreeNode = {
  ref: string
  kind: string
  title: string
  status: string
  depth: number
  doneCount: number
  reviewCount: number
  totalCount: number
  disagrees: boolean
}

export type Tree = { ref: string; title: string; nodes: TreeNode[] }

export type JournalEntry = {
  seq: number
  ref: string | null
  at: string
  actor: string
  role: string
  kind: string
  summary: string | null
}

export type Journal = { cursor: number; hasMore: boolean; events: JournalEntry[] }

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

async function get(path: string): Promise<Response> {
  const response = await fetch(path, { credentials: 'same-origin' })

  if (response.status === 401) {
    setSignedIn(false)
    throw new Unauthorized()
  }

  if (!response.ok) {
    throw new Error(`${path} → ${response.status}`)
  }

  setSignedIn(true)
  return response
}

const json = async <T,>(path: string): Promise<T> => (await get(path)).json() as Promise<T>

export const projects = () => json<Project[]>('/api/projects')

export const watch = (project?: string) =>
  json<Watch>(project ? `/api/watch?project=${encodeURIComponent(project)}` : '/api/watch')

export const tree = (run: string) => json<Tree>(`/api/runs/${encodeURIComponent(run)}/tree`)

/** `text/plain`, and it stays text all the way to the `<pre>`. Never parsed, never re-encoded. */
export const brief = async (run: string): Promise<string> =>
  (await get(`/api/runs/${encodeURIComponent(run)}/brief`)).text()

export const journal = (run: string, since?: number, limit?: number) => {
  const query = new URLSearchParams()
  if (since !== undefined && since > 0) query.set('since', String(since))
  if (limit !== undefined) query.set('limit', String(limit))
  const suffix = query.size > 0 ? `?${query}` : ''
  return json<Journal>(`/api/runs/${encodeURIComponent(run)}/journal${suffix}`)
}

export async function signIn(username: string, password: string): Promise<boolean> {
  const response = await fetch('/api/session', {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })

  if (response.status === 204) {
    setSignedIn(true)
    return true
  }

  return false
}

export async function signOut(): Promise<void> {
  await fetch('/api/session', { method: 'DELETE', credentials: 'same-origin' })
  setSignedIn(false)
}
