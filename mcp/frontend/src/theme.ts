import { createSignal } from 'solid-js'

export type Theme = 'dark' | 'light' | 'system'

const KEY = 'workstate.theme'

function stored(): Theme {
  const value = localStorage.getItem(KEY)
  return value === 'dark' || value === 'light' ? value : 'system'
}

const [theme, set] = createSignal<Theme>(stored())
export { theme }

export function applyTheme(next: Theme): void {
  set(next)

  if (next === 'system') {
    localStorage.removeItem(KEY)
    document.documentElement.removeAttribute('data-theme')
    return
  }

  localStorage.setItem(KEY, next)
  document.documentElement.setAttribute('data-theme', next)
}

/** Three states, cycled in one control — there is no settings page to put a pair of radios on. */
export function cycleTheme(): void {
  applyTheme(theme() === 'system' ? 'dark' : theme() === 'dark' ? 'light' : 'system')
}

export const initTheme = (): void => applyTheme(stored())
