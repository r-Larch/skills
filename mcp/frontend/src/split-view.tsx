import { Show, createEffect, createSignal, onCleanup, onMount, type JSX } from 'solid-js'
import { NUDGE, PANEL_MIN, SHEET_MAX, clampWidth, docks, maxWidth, panelWidth, rememberWidth, rememberedWidth } from './split.ts'

/**
 * The main content and a panel on its right: docked beside it with a drag handle when the measured
 * width holds both, a sheet over it with a title row and a close when it does not. The main content
 * scrolls with `<main>`; the docked panel stays at full height and scrolls on its own.
 */
export function SplitView(props: {
  main: JSX.Element
  side: JSX.Element
  /** The sheet's title row. */
  title: string
  /** Undocked, the sheet shows only while this holds (an item is selected). */
  open: boolean
  onClose: () => void
  /** Changes when the panel shows something else, which starts it at the top. */
  showing: string
}) {
  let root!: HTMLDivElement
  let side: HTMLElement | undefined
  const [width, setWidth] = createSignal(0)
  const [preferred, setPreferred] = createSignal(rememberedWidth())
  const [dragging, setDragging] = createSignal(false)

  onMount(() => {
    const measure = () => setWidth(root.clientWidth)
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(root)
    onCleanup(() => observer.disconnect())
  })

  // Unmeasured is docked: the common case, and no sheet flashes over the list on the first frame.
  const docked = () => width() === 0 || docks(width())
  const size = () => panelWidth(preferred(), width())

  const prefer = (value: number | undefined) => {
    const next = value === undefined ? undefined : clampWidth(value, width())
    setPreferred(next)
    rememberWidth(next)
  }

  createEffect(() => {
    void props.showing
    if (side) side.scrollTop = 0
  })

  createEffect(() => {
    if (docked() || !props.open) return
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape') props.onClose()
    }
    window.addEventListener('keydown', key)
    onCleanup(() => window.removeEventListener('keydown', key))
  })

  // The panel is on the right, so dragging left grows it.
  const down = (e: PointerEvent) => {
    if (e.button !== 0) return
    e.preventDefault()
    const handle = e.currentTarget as HTMLElement
    const startX = e.clientX
    const start = size()
    handle.setPointerCapture(e.pointerId)
    setDragging(true)
    const move = (m: PointerEvent) => prefer(start - (m.clientX - startX))
    const end = () => {
      setDragging(false)
      handle.removeEventListener('pointermove', move)
      handle.removeEventListener('pointerup', end)
      handle.removeEventListener('pointercancel', end)
    }
    handle.addEventListener('pointermove', move)
    handle.addEventListener('pointerup', end)
    handle.addEventListener('pointercancel', end)
  }

  const key = (e: KeyboardEvent) => {
    if (e.key === 'ArrowLeft') prefer(size() + NUDGE)
    else if (e.key === 'ArrowRight') prefer(size() - NUDGE)
    else if (e.key === 'Home') prefer(undefined)
    else return
    e.preventDefault()
  }

  return (
    <div class={`split${docked() ? ' docked' : ''}`} ref={root}>
      <div class="split-main">{props.main}</div>
      <Show when={docked() || props.open}>
        <Show when={docked()}>
          <div
            class={`split-handle${dragging() ? ' on' : ''}`}
            role="separator"
            aria-orientation="vertical"
            aria-label="Resize the detail panel"
            aria-valuenow={size()}
            aria-valuemin={PANEL_MIN}
            aria-valuemax={maxWidth(width())}
            tabindex={0}
            title="Drag, or focus and use the arrow keys, to resize. Double-click resets."
            onPointerDown={down}
            onKeyDown={key}
            onDblClick={() => prefer(undefined)}
          />
        </Show>
        <aside
          class={`split-side${docked() ? '' : ' sheet'}`}
          style={{ width: `${docked() ? size() : Math.min(width(), SHEET_MAX)}px` }}
          aria-label={props.title}
          ref={side}
        >
          <Show when={!docked()}>
            <div class="sheet-h">
              <span class="sheet-t">{props.title}</span>
              <button type="button" class="proj-act" onClick={() => props.onClose()}>
                Close
              </button>
            </div>
          </Show>
          {props.side}
        </aside>
      </Show>
    </div>
  )
}
