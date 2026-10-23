const KEY = 'workstate.theme'

/** Storage can throw outright (a private window, blocked site data); a theme is not worth a crash. */
function stored(): 'dark' | 'light' | null {
  try {
    const value = localStorage.getItem(KEY)
    return value === 'dark' || value === 'light' ? value : null
  } catch {
    return null
  }
}

/** No stored choice leaves `data-theme` unset, so the CSS follows `prefers-color-scheme`. */
export function initTheme(): void {
  const theme = stored()
  if (theme !== null) document.documentElement.dataset.theme = theme
}

/** Flips whatever is showing now, explicit or inherited from the system, and remembers it. */
export function toggleTheme(): void {
  const root = document.documentElement
  const dark = root.dataset.theme
    ? root.dataset.theme === 'dark'
    : matchMedia('(prefers-color-scheme: dark)').matches
  const next = dark ? 'light' : 'dark'

  root.dataset.theme = next
  try {
    localStorage.setItem(KEY, next)
  } catch {
    // remembered for this page only
  }
}
