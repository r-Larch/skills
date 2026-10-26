import type { Board, BoardRow } from './api.ts'
import { CLOSED, rowState, tally, type Tally } from './state.ts'

export type Filter = 'all' | 'open' | 'attention'

export const FILTERS: [Filter, string][] = [
  ['all', 'All'],
  ['open', 'Open'],
  ['attention', 'Needs attention'],
]

export const asFilter = (value: string | undefined): Filter =>
  value === 'open' || value === 'attention' ? value : 'all'

function keep(filter: Filter, row: BoardRow): boolean {
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
 * subtree flattened beneath it. Row 0 of a board is the run itself and is never listed.
 */
export function plan(board: Board, filter: Filter): PlanGroup[] {
  const rows = board.rows
  const loose: PlanRow[] = []
  const phases: PlanGroup[] = []

  for (let i = 1; i < rows.length; i++) {
    const top = rows[i]!
    if (top.depth !== 1) continue

    const below = descendants(rows, i)

    if (top.item.kind === 'phase') {
      const inner = below.filter((r) => keep(filter, r)).map((row) => ({ row, indent: row.depth - 2 }))
      if (inner.length === 0 && filter !== 'all') continue
      phases.push({ head: top, tally: tally(below), rows: inner })
      continue
    }

    for (const row of [top, ...below]) {
      if (keep(filter, row)) loose.push({ row, indent: row.depth - 1 })
    }
  }

  return loose.length > 0 ? [{ head: null, tally: null, rows: loose }, ...phases] : phases
}
