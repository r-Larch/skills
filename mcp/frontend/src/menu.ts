import type { ProjectOverview } from './api.ts'
import { ACCOUNT, ADMIN } from './nav.ts'

/** The width at and below which the sidebar folds behind the top bar's Menu button; `index.css` uses the same. */
export const PHONE_QUERY = '(max-width: 760px)'

const FIXED: Record<string, string> = {
  '/': 'Overview',
  '/inbox': 'Needs you',
  '/activity': 'Activity',
  '/sessions': 'Sessions',
  [ACCOUNT]: 'Account',
}

/** What the top bar calls the page at `pathname`: a run or project by its title once the overview has it. */
export function pageName(pathname: string, projects: ProjectOverview[] | undefined): string {
  const fixed = FIXED[pathname]
  if (fixed) return fixed
  if (pathname === ADMIN || pathname.startsWith(`${ADMIN}/`)) return 'Admin'

  const [, section, id, rest] = pathname.split('/')
  if (section === 'runs' && id) {
    const run = projects?.flatMap((p) => p.runs).find((r) => r.item.id === id)
    return run?.item.title ?? 'Run'
  }

  if (section === 'projects' && id) {
    const title = projects?.find((p) => p.item.id === id)?.item.title ?? 'Project'
    return rest === 'backlog' ? `${title} · Backlog` : title
  }

  if (section === 'items' && id) return 'Item'
  return 'WorkState'
}

/** The menu closes on any move to another page, a query change included; a re-render on the same URL keeps it. */
export const closesOnMove = (from: string, to: string): boolean => from !== to
