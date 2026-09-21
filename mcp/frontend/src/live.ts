import { createSignal } from 'solid-js'

export type LinkState = 'connecting' | 'live' | 'down'

const [lastSeq, setLastSeq] = createSignal(0)
const [pulse, setPulse] = createSignal(0)
const [link, setLink] = createSignal<LinkState>('connecting')

export { lastSeq, pulse, link }

/**
 * The reopen delay for the one case the browser will not retry itself: a non-200 on the stream
 * (an expired cookie, or the API restarting) closes the EventSource for good. This is not a poll —
 * nothing asks the server for board state on a timer; it re-establishes the one push channel, which
 * is what the browser already does at ~3s for a dropped connection.
 */
const REOPEN_DELAY = 5000

/**
 * The single live channel (doc 09 §8). The frame carries only `seq` and `kind` by design, so a
 * pulse is an instruction to re-read — the watch board re-reads `/api/watch`, the run view asks the
 * journal for everything after its cursor. Nothing here holds board state.
 */
export function startStream(): () => void {
  let source: EventSource | null = null
  let timer: ReturnType<typeof setTimeout> | undefined
  let stopped = false
  let everOpened = false

  const open = () => {
    if (stopped) return

    setLink('connecting')
    source = new EventSource('/api/stream', { withCredentials: true })

    source.onopen = () => {
      setLink('live')

      // A reconnect may have missed events. Nudge the views: each one recovers through its own
      // cursor, which is what U6 built `?since=` for.
      if (everOpened) setPulse((n) => n + 1)
      everOpened = true
    }

    source.addEventListener('work', (event) => {
      const frame = parse((event as MessageEvent<string>).data)
      if (frame === undefined) return

      setLastSeq((seen) => (frame.seq > seen ? frame.seq : seen))
      setPulse((n) => n + 1)
    })

    source.onerror = () => {
      if (source?.readyState === EventSource.CLOSED) {
        // The browser has given up on this one. Everything else it retries by itself.
        setLink('down')
        source.close()
        source = null
        timer = setTimeout(open, REOPEN_DELAY)
        return
      }

      setLink('connecting')
    }
  }

  open()

  return () => {
    stopped = true
    clearTimeout(timer)
    source?.close()
  }
}

function parse(data: string): { seq: number; kind: string } | undefined {
  try {
    const frame = JSON.parse(data) as { seq?: number; kind?: string }
    return typeof frame.seq === 'number' ? { seq: frame.seq, kind: frame.kind ?? 'unknown' } : undefined
  } catch {
    return undefined
  }
}
