import type { Attention } from './api'
import { UNKNOWN } from './fill.ts'

/** How many rows of a rank the operator sees before asking for the rest. */
export const PREVIEW = 5

export type Group = { rank: string; rows: Attention[]; runs: number }

/**
 * The store's own rank vocabulary (`WorkState.Core/Rendering/Attention.cs`), mapped to the one hue
 * each rank's meaning earns under doc 09 §3. An unknown rank falls through to no hue rather than a
 * guess: "absent means normal" is safer than inventing urgency for a rank added after this table.
 */
export const TONE: Record<string, string> = {
  expired_claim: 'attention',
  blocker_finding: 'refused',
  phase_gate_not_run: 'attention',
  done_unverified: 'refused',
  assumption_past_revisit: 'attention',
  stale_in_progress: 'attention',
}

export const runOf = (qualified: string | null): string | null => qualified?.split('/')[0] ?? null

/**
 * Grouped by rank, in the order the ranks arrive.
 *
 * That order is not a choice made here: doc 06 §5 sorts the queue by cost-of-ignoring and the server
 * sends it sorted, so first-appearance order *is* the ranking. Re-sorting in the client would be a
 * second, divergent opinion about urgency — and the measured queue is 106 rows over two ranks, so
 * grouping turns an unreadable dump into two headings the operator can take one at a time.
 */
export function group(rows: Attention[]): Group[] {
  const order: string[] = []
  const byRank = new Map<string, Attention[]>()

  for (const row of rows) {
    let bucket = byRank.get(row.rank)
    if (bucket === undefined) {
      bucket = []
      byRank.set(row.rank, bucket)
      order.push(row.rank)
    }
    bucket.push(row)
  }

  return order.map((rank) => {
    const group = byRank.get(rank) ?? []
    return {
      rank,
      rows: group,
      runs: new Set(group.map((r) => runOf(r.qualifiedRef)).filter((r) => r !== null)).size,
    }
  })
}

export type HonestyCell = { n: string; tone: string; label: string; note: string }

type Honesty = {
  doneUnverified: number
  forced: number
  ungated: number
  followupsOpen: number
}

/**
 * doc 09 §5. Four cells, and the fourth is deliberately unlike the other three: an open follow-up is
 * doc 07 §4 working, not an accusation, so it carries no hue. Nor does a zero — a count that needs no
 * decision gets no colour, which is doc 06 rule 4 applied to the one strip that exists to break it.
 * (This is where the prototype is diverged from: it paints `0 forced` green, and green is reserved
 * for evidence somebody other than the author ran.)
 *
 * `undefined` renders `—`, never `0`. This is the strip the product is named for, and four zeroes on
 * a first paint say "nothing is being closed dishonestly" — the one sentence it exists to refuse to
 * say without evidence. Unknown is not clean.
 */
export function honestyCells(h: Honesty | undefined): HonestyCell[] {
  const cell = (
    n: number | undefined,
    hue: string,
    label: string,
    note: string,
  ): HonestyCell => ({
    n: n === undefined ? UNKNOWN : String(n),
    tone: n !== undefined && n > 0 ? hue : '',
    label,
    note,
  })

  return [
    cell(
      h?.doneUnverified,
      'refused',
      'closed with no independent verification',
      'Someone marked it done. Nobody but the author checked.',
    ),
    cell(
      h?.forced,
      'refused',
      'forced closures',
      'Overrides are permitted and permanently recorded.',
    ),
    cell(
      h?.ungated,
      'attention',
      'phases finished but never gated',
      'Every task closed; the composition review never ran.',
    ),
    cell(
      h?.followupsOpen,
      '',
      'follow-ups still open',
      'Deferred work that went somewhere real instead of vanishing.',
    ),
  ]
}
