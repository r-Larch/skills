import type { Board, BoardRow } from './api.ts'
import { CLOSED, rowState, tally, type Tally } from './state.ts'
import { SHOW_CANCELLED } from './nav.ts'

export type Filter = 'all' | 'open' | 'attention'

export const FILTERS: [Filter, string][] = [
  ['open', 'Open'],
  ['all', 'All'],
  ['attention', 'Needs attention'],
]

/** The filter a run page opens on, and the one its URL leaves out. */
export const DEFAULT_FILTER: Filter = 'open'

export const asFilter = (value: string | undefined): Filter =>
  value === 'all' || value === 'attention' ? value : DEFAULT_FILTER

/** A run page's query: only what differs from the defaults, so an explicit `filter=all` survives a link. */
export function planQuery(filter: Filter, tab: string, cancelled = false): string {
  const q = new URLSearchParams()
  if (filter !== DEFAULT_FILTER) q.set('filter', filter)
  if (tab !== 'overview') q.set('tab', tab)
  if (cancelled) q.set('cancelled', SHOW_CANCELLED)
  return q.size > 0 ? `?${q}` : ''
}

function keep(filter: Filter, row: BoardRow, cancelled: boolean): boolean {
  if (row.item.status === 'cancelled' && !cancelled) return false
  if (filter === 'all') return true
  if (filter === 'open') return !CLOSED.has(row.item.status)
  const k = rowState(row).k
  return k === 'stop' || k === 'claimed'
}

/** The rows strictly below `index` in a pre-order list: everything until depth climbs back. */
function descendants(rows: BoardRow[], index: number): BoardRow[] {
  const root = rows[index]
  if (root === undefined) return []

  const out: BoardRow[] = []
  for (let i = index + 1; i < rows.length; i++) {
    const row = rows[i]!
    if (row.depth <= root.depth) break
    out.push(row)
  }
  return out
}

type PlanRow = { row: BoardRow; indent: number }

type PlanGroup = { head: BoardRow | null; tally: Tally | null; rows: PlanRow[] }

/**
 * The plan list: loose top-level work first under "Tasks", then one group per phase with its whole
 * subtree flattened beneath it. Row 0 of a board is the run itself and is never listed. Cancelled
 * rows are left out unless `cancelled` is set, an emptied cancelled phase with them.
 */
export function plan(board: Board, filter: Filter, cancelled = false): PlanGroup[] {
  const rows = board.rows
  const loose: PlanRow[] = []
  const phases: PlanGroup[] = []

  for (let i = 1; i < rows.length; i++) {
    const top = rows[i]!
    if (top.depth !== 1) continue

    const below = descendants(rows, i)

    if (top.item.kind === 'phase') {
      const inner = below.filter((r) => keep(filter, r, cancelled)).map((row) => ({ row, indent: row.depth - 2 }))
      if (inner.length === 0 && (filter !== 'all' || !keep(filter, top, cancelled))) continue
      phases.push({ head: top, tally: tally(below), rows: inner })
      continue
    }

    for (const row of [top, ...below]) {
      if (keep(filter, row, cancelled)) loose.push({ row, indent: row.depth - 1 })
    }
  }

  return loose.length > 0 ? [{ head: null, tally: null, rows: loose }, ...phases] : phases
}

const listed = (groups: PlanGroup[]) => groups.reduce((n, g) => n + g.rows.length + (g.head ? 1 : 0), 0)

/** How many cancelled lines the plan would add under All: the number the toggle shows. */
export const cancelledLines = (board: Board): number =>
  listed(plan(board, 'all', true)) - listed(plan(board, 'all', false))

/** A child list with its cancelled entries out unless `cancelled` is set, and how many it has. */
export function withoutCancelled<T extends { item: { status: string } }>(list: T[], cancelled: boolean): { shown: T[]; count: number } {
  const open = list.filter((c) => c.item.status !== 'cancelled')
  return { shown: cancelled ? list : open, count: list.length - open.length }
}
