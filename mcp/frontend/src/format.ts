/** No answer yet. A count is never `0` before the request that knows it has returned. */
export const UNKNOWN = '—'

/** `5m`, `3h`, `2d` — the prototype's one duration scale. */
function dur(seconds: number): string {
  const s = Math.abs(seconds)
  if (s < 90) return `${Math.round(s)}s`
  if (s < 5400) return `${Math.round(s / 60)}m`
  if (s < 172800) return `${Math.round(s / 3600)}h`
  return `${Math.round(s / 86400)}d`
}

/** Relative to the server's `asOf`, not this machine's clock. */
export function ago(iso: string | null | undefined, asOf: string): string {
  if (!iso) return ''
  const s = (Date.parse(asOf) - Date.parse(iso)) / 1000
  if (Number.isNaN(s)) return ''
  return s < 0 ? `in ${dur(-s)}` : `${dur(s)} ago`
}

export const clock = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })

export const day = (iso: string) => new Date(iso).toLocaleDateString([], { day: 'numeric', month: 'short' })

/** Run titles carry their ref as a prefix (`M7 — The dashboard`); the ref is shown beside it anyway. */
export const short = (title: string) => title.replace(/^[A-Z0-9]+ — /, '')

export const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

export const plural = (n: number, one: string, many = `${one}s`) => (n === 1 ? one : many)
