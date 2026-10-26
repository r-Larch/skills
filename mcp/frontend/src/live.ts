import { createSignal } from 'solid-js'

type LinkState = 'connecting' | 'live' | 'down'

/** doc 10's frame. `runId: null` with `kind: 'reconnect'` is ours: anything may have been missed. */
type Frame = { seq: number; runId: string | null; itemId: string | null; kind: string }

const [link, setLink] = createSignal<LinkState>('connecting')
export { link }

const listeners = new Set<(frames: Frame[]) => void>()

/** Frames arrive in bursts (one write is often several events); readers hear about a burst once. */
const DEBOUNCE = 400

/**
 * The reopen delay for the one case the browser will not retry itself: a non-200 on the stream
 * (an expired cookie, or the API restarting) closes the EventSource for good.
 */
const REOPEN_DELAY = 5000

export function onFrames(listener: (frames: Frame[]) => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/**
 * The single live channel. A frame is an instruction to re-read, never the state itself: nothing
 * here holds board data, and nothing asks the server for it on a timer.
 */
export function startStream(): () => void {
  let source: EventSource | null = null
  let reopen: ReturnType<typeof setTimeout> | undefined
  let flush: ReturnType<typeof setTimeout> | undefined
  let pending: Frame[] = []
  let stopped = false
  let everOpened = false

  const push = (frame: Frame) => {
    pending.push(frame)
    clearTimeout(flush)
    flush = setTimeout(() => {
      const burst = pending
      pending = []
      for (const listener of listeners) listener(burst)
    }, DEBOUNCE)
  }

  const open = () => {
    if (stopped) return

    setLink('connecting')
    source = new EventSource('/api/stream', { withCredentials: true })

    source.onopen = () => {
      setLink('live')
      if (everOpened) push({ seq: 0, runId: null, itemId: null, kind: 'reconnect' })
      everOpened = true
    }

    source.addEventListener('work', (event) => {
      const frame = parse((event as MessageEvent<string>).data)
      if (frame !== undefined) push(frame)
    })

    source.onerror = () => {
      if (source?.readyState === EventSource.CLOSED) {
        setLink('down')
        source.close()
        source = null
        reopen = setTimeout(open, REOPEN_DELAY)
        return
      }

      setLink('connecting')
    }
  }

  open()

  return () => {
    stopped = true
    clearTimeout(reopen)
    clearTimeout(flush)
    source?.close()
  }
}

function parse(data: string): Frame | undefined {
  try {
    const frame = JSON.parse(data) as Partial<Frame>
    if (typeof frame.seq !== 'number') return undefined
    return { seq: frame.seq, runId: frame.runId ?? null, itemId: frame.itemId ?? null, kind: frame.kind ?? 'unknown' }
  } catch {
    return undefined
  }
}
