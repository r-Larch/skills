import { createSignal, onCleanup } from 'solid-js'

/**
 * doc 09 §6: lease clocks tick and nothing else animates. `prefers-reduced-motion` freezes them to
 * a static remaining-time string, so this is the one query the whole app makes.
 */
export const reducedMotion = (): boolean =>
  typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches

/**
 * One interval for every clock on the page. A second per row would be N timers rendering the same
 * instant, and they would drift apart on a busy tab.
 */
export function ticker(): () => number {
  const [now, setNow] = createSignal(Date.now())

  if (reducedMotion()) return now

  const handle = setInterval(() => setNow(Date.now()), 1000)
  onCleanup(() => clearInterval(handle))

  return now
}

/**
 * The browser's clock is not the server's, and the difference is exactly the size of the error a
 * countdown would show. `asOf` is sent for this: the offset is measured once against the response
 * that carried it, and every remaining-time is then computed in server time.
 */
export function skewFrom(asOf: string): number {
  const server = Date.parse(asOf)
  return Number.isNaN(server) ? 0 : server - Date.now()
}

/** Milliseconds left on a lease, in the server's frame of reference. Negative once it has lapsed. */
export function remainingMs(expiresAt: string, now: number, skew: number): number {
  const expiry = Date.parse(expiresAt)
  return Number.isNaN(expiry) ? 0 : expiry - (now + skew)
}

/** `18:42`, and `−13:02` once past zero — the clock keeps counting, because a lapsed lease is a state. */
export function clockFace(ms: number): string {
  const total = Math.floor(Math.abs(ms) / 1000)
  const sign = ms < 0 ? '−' : ''
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = total % 60

  const pad = (n: number) => (n < 10 ? `0${n}` : String(n))

  return hours > 0
    ? `${sign}${hours}:${pad(minutes)}:${pad(seconds)}`
    : `${sign}${minutes}:${pad(seconds)}`
}

/** The frozen form. No seconds, because a second that never changes reads as a broken clock. */
export function staticFace(ms: number): string {
  const minutes = Math.round(Math.abs(ms) / 60000)
  const unit = minutes === 1 ? 'minute' : 'minutes'
  return ms < 0 ? `${minutes} ${unit} past` : `${minutes} ${unit} left`
}

/** `11:24` in the reader's own zone — the journal is the one pane whose job is human time. */
export function timeOfDay(iso: string): string {
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return '—'
  return `${at.getHours() < 10 ? '0' : ''}${at.getHours()}:${at.getMinutes() < 10 ? '0' : ''}${at.getMinutes()}`
}

export function dayOf(iso: string): string {
  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return ''
  return at.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}
