import type { JournalEntry } from './api'

/**
 * Where a run's "last looked" cursor is remembered.
 *
 * A function of an explicit string, never of a router param. The first version of this pane took the
 * run from `props.run` at cleanup time — after the route had already changed — and wrote
 * `workstate.seen.undefined`, so the key it read on mount and the key it wrote on leaving were never
 * the same and the divider could not render. The key is now computed once, at creation, and the same
 * string is used for both.
 */
export const seenKey = (run: string): string => `workstate.seen.${run}`

export const newestFirst = (events: readonly JournalEntry[]): JournalEntry[] =>
  [...events].sort((a, b) => b.seq - a.seq)

export const newestSeq = (events: readonly JournalEntry[]): number =>
  events.reduce((max, event) => (event.seq > max ? event.seq : max), 0)

/**
 * Where the "since you last looked" divider goes, as an index into a newest-first list: the divider
 * renders immediately *before* that row, and everything above it arrived since the last visit.
 *
 * - `undefined` — draw no divider. Either this run has never been looked at (nothing to be since),
 *   or the newest event was already seen (nothing is new, and a divider at the top would be a lie).
 * - `events.length` — every loaded event is newer than the cursor, so the line goes below them all.
 */
export function dividerIndex(events: readonly JournalEntry[], seen: number): number | undefined {
  if (seen <= 0 || events.length === 0) return undefined

  const index = events.findIndex((event) => event.seq <= seen)

  if (index === 0) return undefined
  return index < 0 ? events.length : index
}

/** Events arrive ascending and may repeat across a catch-up page; seq is the identity. */
export function merge(
  held: readonly JournalEntry[],
  arriving: readonly JournalEntry[],
): JournalEntry[] {
  const known = new Set(held.map((event) => event.seq))
  return [...held, ...arriving.filter((event) => !known.has(event.seq))]
}
