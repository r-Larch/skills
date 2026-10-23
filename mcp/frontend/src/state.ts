import type { BoardRow, ItemPage, ItemRef } from './api.ts'

/** doc 10's Rollup leaves: the kinds that are a unit of work rather than a container of it. */
export const LEAF = new Set(['task', 'followup', 'subtask', 'spike', 'question', 'incident'])

export const CLOSED = new Set(['done', 'cancelled', 'superseded'])

export type StateKey = 'gone' | 'stop' | 'doing' | 'proven' | 'claimed' | 'wait' | 'idle'

export type State = { k: StateKey; label: string }

/**
 * What `state()` needs to know, and nothing more. `proof` is three-valued: `null` means the caller
 * holds no verification data at all (an `ItemRef` alone), which is not the same as "none exists".
 */
export type StateInput = {
  kind: string
  status: string
  resolution: string | null
  claimExpired: boolean
  proof: boolean | null
  waitingOn: string[]
}

/** Independent means someone other than the author ran it: a worker's own run is a claim. */
export const independent = (v: { passed: boolean; runBy: string } | null | undefined): boolean =>
  v != null && v.passed && v.runBy !== 'worker'

/** One vocabulary of state, used by every view. The order of the tests is the precedence. */
export function state(i: StateInput): State {
  if (i.status === 'superseded') return { k: 'gone', label: 'Superseded' }
  if (i.status === 'cancelled') return { k: 'gone', label: 'Cancelled' }
  if (i.status === 'blocked') return { k: 'stop', label: 'Blocked' }
  if (i.claimExpired && !CLOSED.has(i.status)) return { k: 'stop', label: 'Claim expired' }
  if (i.status === 'in_progress') return { k: 'doing', label: 'In progress' }
  if (i.status === 'review') return { k: 'doing', label: 'In review' }

  if (i.status === 'done') {
    if (i.resolution === 'obsolete') return { k: 'gone', label: 'Moved' }
    if (!LEAF.has(i.kind)) return { k: 'proven', label: 'Done' }
    if (i.proof === true) return { k: 'proven', label: 'Verified' }
    if (i.proof === null) return { k: 'claimed', label: 'Done' }
    return { k: 'claimed', label: 'Done, not verified' }
  }

  if (i.waitingOn.length > 0) return { k: 'wait', label: `Waiting on ${i.waitingOn.join(', ')}` }
  return { k: 'idle', label: 'Open' }
}

const rowInput = (row: BoardRow): StateInput => ({
  kind: row.item.kind,
  status: row.item.status,
  resolution: row.resolution,
  claimExpired: row.claim?.expired === true,
  proof: independent(row.verification),
  waitingOn: row.blockedBy.map((b) => b.qualifiedRef),
})

export const rowState = (row: BoardRow): State => state(rowInput(row))

export function pageInput(page: ItemPage): StateInput {
  const latest = [...page.verifications].sort((a, b) => b.at.localeCompare(a.at))[0]

  return {
    kind: page.item.kind,
    status: page.item.status,
    resolution: page.resolution,
    claimExpired: page.claim?.expired === true,
    proof: independent(latest),
    waitingOn: page.links.in
      .filter((l) => l.type === 'blocks' && !CLOSED.has(l.item.status))
      .map((l) => l.item.qualifiedRef),
  }
}

/** For a bare `ItemRef`, where nothing says whether it was verified or what it waits on. */
export const refState = (ref: ItemRef): State =>
  state({ kind: ref.kind, status: ref.status, resolution: null, claimExpired: false, proof: null, waitingOn: [] })

export type Tally = Record<StateKey, number> & { total: number; done: number; leaves: BoardRow[] }

/** The segmented bar's numbers: every leaf under `rows`, gone ones counted but never drawn. */
export function tally(rows: BoardRow[]): Tally {
  const t: Tally = { gone: 0, stop: 0, doing: 0, proven: 0, claimed: 0, wait: 0, idle: 0, total: 0, done: 0, leaves: [] }

  for (const row of rows) {
    if (!LEAF.has(row.item.kind)) continue
    const k = rowState(row).k
    t[k] += 1
    if (k === 'gone') continue
    t.total += 1
    t.leaves.push(row)
  }

  t.done = t.proven + t.claimed
  return t
}
