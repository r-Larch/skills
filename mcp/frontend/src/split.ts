/**
 * The run page's split, as numbers: the plan list is the page, the item detail a panel docked on
 * the right. Every input is a measured width of the run area, never the window: the sidebar, a
 * zoom and a narrow browser all change how much room there really is.
 */

/** The centre column, page gutters included: below this the list stops being readable. */
export const LIST_MIN = 440

/** A docked panel never goes below this, whatever was dragged or remembered. */
export const PANEL_MIN = 320

/** The handle's hit area. The line drawn inside it is 1px. */
export const HANDLE = 9

/** One arrow press on the focused handle. */
export const NUDGE = 32

/** The share of the room the panel takes before anyone drags it. */
export const PANEL_SHARE = 0.55

/** A sheet is never wider than this, however wide the run area. */
export const SHEET_MAX = 640

export const WIDTH_KEY = 'workstate.run-panel-width'

/** Docked when the list, the handle and the panel all fit at their minimum; otherwise a sheet. */
export const docks = (width: number): boolean => width >= LIST_MIN + HANDLE + PANEL_MIN

/** The widest the panel may be: the list keeps its minimum, the panel yields. */
export const maxWidth = (width: number): number => Math.max(PANEL_MIN, width - HANDLE - LIST_MIN)

export const clampWidth = (want: number, width: number): number =>
  Math.round(Math.min(maxWidth(width), Math.max(PANEL_MIN, want)))

/** The panel's width: what the user chose, or the default share, always inside both floors. */
export const panelWidth = (preferred: number | undefined, width: number): number =>
  clampWidth(preferred ?? (width - HANDLE) * PANEL_SHARE, width)

type Store = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

/** `localStorage` itself can throw on access (a private window, blocked site data). */
const local = (): Store => localStorage

/** The remembered width, or `undefined` for "no preference" when nothing usable is stored. */
export function rememberedWidth(store: () => Store = local): number | undefined {
  try {
    const raw = store().getItem(WIDTH_KEY)
    const value = raw === null ? NaN : Number(raw)
    return Number.isFinite(value) && value > 0 ? value : undefined
  } catch {
    return undefined
  }
}

/** `undefined` forgets the preference, so the default share applies again. */
export function rememberWidth(value: number | undefined, store: () => Store = local): void {
  try {
    if (value === undefined) store().removeItem(WIDTH_KEY)
    else store().setItem(WIDTH_KEY, String(Math.round(value)))
  } catch {
    // remembered for this page only
  }
}
