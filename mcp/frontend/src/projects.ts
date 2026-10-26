import { createSignal } from 'solid-js'
import type { ProjectOverview, RunOverview } from './api.ts'
import { projectName } from './format.ts'
import { CLOSED } from './state.ts'

/** A project with an open run, or with activity this recent, is Active; the rest are Quiet. */
export const ACTIVE_DAYS = 14

/**
 * Open: not closed by status, and not finished in fact — a run whose every task is done (5/5) is
 * listed with the closed ones even before anyone closes it. An open run with no tasks yet is open.
 */
export function runIsOpen(run: RunOverview): boolean {
  if (CLOSED.has(run.item.status)) return false
  const r = run.rollup
  return r.total === 0 || r.open + r.inProgress + r.review + r.blocked > 0
}

export type ProjectGroup = 'active' | 'quiet' | 'archived'

export function projectGroup(p: ProjectOverview, asOf: string): ProjectGroup {
  if (p.archivedAt) return 'archived'
  if (p.runs.some(runIsOpen)) return 'active'
  if (!p.lastActivityAt) return 'quiet'
  return Date.parse(asOf) - Date.parse(p.lastActivityAt) <= ACTIVE_DAYS * 86_400_000 ? 'active' : 'quiet'
}

/** Newest activity first, never-active last, ref breaking ties — the order the overview serves. */
export function byActivity(a: ProjectOverview, b: ProjectOverview): number {
  const at = (p: ProjectOverview) => (p.lastActivityAt ? Date.parse(p.lastActivityAt) : -Infinity)
  return at(b) - at(a) || a.item.ref.localeCompare(b.item.ref)
}

/** Matches the name, the full title and the ref, ignoring case. */
export function matchesQuery(p: ProjectOverview, query: string): boolean {
  const q = query.trim().toLowerCase()
  if (q === '') return true
  return [projectName(p.item), p.item.title, p.item.ref].some((s) => s.toLowerCase().includes(q))
}

const KEY = 'workstate.archived-open'

/** Storage can throw outright (a private window, blocked site data); the toggle then lasts the page. */
function storedOpen(): boolean {
  try {
    return localStorage.getItem(KEY) === '1'
  } catch {
    return false
  }
}

const [archivedOpen, setArchivedOpenSignal] = createSignal(storedOpen())
export { archivedOpen }

export function setArchivedOpen(open: boolean): void {
  setArchivedOpenSignal(open)
  try {
    localStorage.setItem(KEY, open ? '1' : '0')
  } catch {
    // remembered for this page only
  }
}
