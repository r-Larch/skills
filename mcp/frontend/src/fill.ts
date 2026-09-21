/**
 * Three states, not two.
 *
 * Every panel on the watch floor used `value?.length ?? 0`, which collapses *not asked yet* into
 * *asked, and the answer is none*. On a first paint that rendered "Nothing needs you." before the
 * request had returned — and doc 09 §1 says this view exists to answer "does anything need me right
 * now?". A supervision console may say no only when it knows. So presence is a state of its own,
 * and it is a function rather than an inline `??` so the collapse cannot be written again by hand.
 *
 * `undefined` means no answer has ever arrived. Once one has, the value is held: a refetch shows the
 * board it last knew rather than blanking, because a momentarily stale count is closer to the truth
 * than "unknown" and much closer than zero.
 */
export type Fill = 'unknown' | 'empty' | 'rows'

export const fill = (value: { length: number } | undefined): Fill =>
  value === undefined ? 'unknown' : value.length === 0 ? 'empty' : 'rows'

/**
 * `—` is already this product's mark for "no value here" — doc 06 §3 uses it where a rollup is
 * meaningless. It is static, so doc 09 §6's "one thing moves" still holds: the lease clock is the
 * only thing on the page that animates, and an unknown panel waits without a spinner.
 */
export const UNKNOWN = '—'

/** A count for a section heading: never `0` before the answer has arrived. */
export const count = (value: { length: number } | undefined): string =>
  value === undefined ? UNKNOWN : String(value.length)

/** The same, with the unit the heading reads with — "— open" is a lie-free "N open". */
export const countOf = (value: { length: number } | undefined, unit: string): string =>
  `${count(value)} ${unit}`

/** Plural by default, since an unknown count is not one of anything. */
export const plural = (
  value: { length: number } | undefined,
  one: string,
  many = `${one}s`,
): string => (value !== undefined && value.length === 1 ? one : many)
