import type { ProjectOverview, SearchHit } from './api.ts'
import { ADMIN, USERS, backlogHref, itemHref, projectHref } from './nav.ts'
import { projectName } from './format.ts'
import { membersHref } from './sharing.ts'

/** A palette command: a page to open, or one of the shell's actions (the theme, a tenant switch). */
export type Command = {
  id: string
  label: string
  /** Matched with the label but not shown: a project's ref and full title. */
  keywords: string
  href: string | null
  action: 'theme' | 'tenant' | null
  /** The workspace a `tenant` command switches to. */
  tenant?: string
}

/** What the signed-in user may open beyond the work pages; without it the palette offers the work pages alone. */
export type PaletteAccount = {
  administers: boolean
  systemAdmin: boolean
  tenants: { id: string; slug: string; current: boolean }[]
}

/**
 * Every command the shell offers: the four pages, the admin pages the user may open, on a project's page its
 * Members, each project and its backlog, a switch to each other workspace they may enter, the theme. `here` is the
 * project whose page is open — from the path, so a workspace admin who is not a member, and so has no card for it,
 * gets the command too.
 */
export function commands(projects: Pick<ProjectOverview, 'item'>[], account?: PaletteAccount, here?: string | null): Command[] {
  const page = (id: string, label: string, href: string): Command => ({ id, label, keywords: '', href, action: null })
  return [
    page('overview', 'Go to Overview', '/'),
    page('inbox', 'Go to Needs you', '/inbox'),
    page('activity', 'Go to Activity', '/activity'),
    page('sessions', 'Go to Sessions', '/sessions'),
    ...(account?.administers ? [{ ...page('admin', 'Go to Admin', ADMIN), keywords: 'workspaces members invites tenants' }] : []),
    ...(account?.systemAdmin ? [{ ...page('users', 'Go to Users', USERS), keywords: 'admin accounts disable' }] : []),
    ...(here ? [{ ...page(`members:${here}`, 'Manage members', membersHref(here)), keywords: 'share sharing access people' }] : []),
    ...projects.flatMap(({ item }) => {
      const keywords = `${item.ref} ${item.title}`
      return [
        { id: `project:${item.id}`, label: `Open project ${projectName(item)}`, keywords, href: projectHref(item.id), action: null },
        { id: `backlog:${item.id}`, label: `Open ${projectName(item)} backlog`, keywords, href: backlogHref(item.id), action: null },
      ]
    }),
    ...(account && account.tenants.length > 1 ? account.tenants : [])
      .filter((t) => !t.current)
      .map((t): Command => ({
        id: `tenant:${t.id}`,
        label: `Switch to workspace ${t.slug}`,
        keywords: 'tenant',
        href: null,
        action: 'tenant',
        tenant: t.id,
      })),
    { id: 'theme', label: 'Toggle theme', keywords: 'dark light', href: null, action: 'theme' },
  ]
}

/**
 * The commands every word of `query` appears in (label or keywords, any case), in their own order
 * except that a label starting with the query comes first. An empty query keeps them all.
 */
export function matchCommands(all: Command[], query: string): Command[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return all
  const q = words.join(' ')
  const hits = all.filter((c) => {
    const text = `${c.label} ${c.keywords}`.toLowerCase()
    return words.every((w) => text.includes(w))
  })
  const leads = (c: Command) => c.label.toLowerCase().startsWith(q) || c.label.toLowerCase().startsWith(`go to ${q}`)
  return [...hits.filter(leads), ...hits.filter((c) => !leads(c))]
}

export type Entry = { kind: 'command'; command: Command } | { kind: 'item'; hit: SearchHit }

/**
 * The rows in the order Up/Down walks them: commands, then items — except that when the query is
 * some item's exact ref, items lead, so Enter on a typed ref opens that item.
 */
export function arrange(matched: Command[], hits: SearchHit[]): { itemsFirst: boolean; entries: Entry[] } {
  const itemsFirst = hits.some((h) => h.match === 'ref')
  const cs: Entry[] = matched.map((command) => ({ kind: 'command', command }))
  const is: Entry[] = hits.map((hit) => ({ kind: 'item', hit }))
  return { itemsFirst, entries: itemsFirst ? [...is, ...cs] : [...cs, ...is] }
}

/** Where Enter on an entry goes; `null` for an action (the theme, a tenant switch), which navigates nowhere. */
export const entryHref = (entry: Entry): string | null =>
  entry.kind === 'item' ? itemHref(entry.hit.item, entry.hit.run) : entry.command.href

type Key = { key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean }

/** Ctrl+P, or Cmd+P on macOS: the browser's print shortcut, which the palette takes over. */
export const isPaletteChord = (e: Key): boolean =>
  (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'p'

/** `/` opens the palette only where it would not be typed into a field. */
export const isSlash = (e: Key, typing: boolean): boolean =>
  e.key === '/' && !typing && !e.ctrlKey && !e.metaKey && !e.altKey

/** The shortcut as this machine's keyboard writes it. */
export const chordLabel = (userAgent: string): string => (/Mac|iPhone|iPad/.test(userAgent) ? '⌘P' : 'Ctrl P')
