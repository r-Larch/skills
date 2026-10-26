import type { Honesty, InboxRow, ProjectOverview } from './api.ts'

/** doc 10's inbox kinds, in the words a person reads them in. */
const INBOX_KINDS: Record<string, { label: string; dot: 'stop' | 'claimed' | 'note' }> = {
  blocked: { label: 'Blocked', dot: 'stop' },
  blocker_finding: { label: 'Blocker finding', dot: 'stop' },
  question: { label: 'Question', dot: 'stop' },
  expired_claim: { label: 'Claim expired', dot: 'stop' },
  stale_in_progress: { label: 'Stalled', dot: 'stop' },
  assumption_due: { label: 'Assumption due', dot: 'claimed' },
  done_unverified: { label: 'Not verified', dot: 'claimed' },
  unconsumed_note: { label: 'Unread note', dot: 'note' },
}

export const inboxKind = (kind: string) => INBOX_KINDS[kind] ?? { label: kind.replace(/_/g, ' '), dot: 'stop' as const }

type InboxGroup = { kind: string; label: string; rows: InboxRow[] }

/** The summary line over the inbox: one count per kind, in the order of `INBOX_KINDS`. */
export function groupInbox(rows: InboxRow[]): InboxGroup[] {
  const order = Object.keys(INBOX_KINDS)
  const groups = new Map<string, InboxRow[]>()
  for (const row of rows) groups.set(row.kind, [...(groups.get(row.kind) ?? []), row])

  return [...groups.entries()]
    .sort(([a], [b]) => (order.indexOf(a) + 1 || 99) - (order.indexOf(b) + 1 || 99))
    .map(([kind, list]) => ({ kind, label: inboxKind(kind).label, rows: list }))
}

type HonestyCell = { n: number; label: string; bad: boolean }

/** The four counts beside "Working now". A zero needs no decision, so it gets no hue. */
export function honestyCells(honesty: Honesty, projects: ProjectOverview[]): HonestyCell[] {
  const open = openTasks(projects)
  return [
    { n: honesty.doneUnverified, label: 'closed without independent proof', bad: honesty.doneUnverified > 0 },
    { n: honesty.forced, label: 'forced closures', bad: honesty.forced > 0 },
    { n: honesty.followupsOpen, label: 'follow-ups open', bad: false },
    { n: open, label: 'tasks open', bad: false },
  ]
}

export const openTasks = (projects: ProjectOverview[]) =>
  projects.reduce((n, p) => n + p.rollup.open + p.rollup.inProgress + p.rollup.review + p.rollup.blocked, 0)
