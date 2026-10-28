import type { ProjectOverview, SearchHit } from './api.ts'
import { backlogHref, itemHref, projectHref } from './nav.ts'
import { projectName } from './format.ts'

/** A palette command: a page to open, or the one action the shell has (the theme). */
export type Command = {
  id: string
  label: string
  /** Matched with the label but not shown: a project's ref and full title. */
  keywords: string
  href: string | null
  action: 'theme' | null
}

/** Every command the shell offers: the four pages, each project and its backlog, the theme. */
export function commands(projects: Pick<ProjectOverview, 'item'>[]): Command[] {
  const page = (id: string, label: string, href: string): Command => ({ id, label, keywords: '', href, action: null })
  return [
    page('overview', 'Go to Overview', '/'),
    page('inbox', 'Go to Needs you', '/inbox'),
    page('activity', 'Go to Activity', '/activity'),
    page('sessions', 'Go to Sessions', '/sessions'),
    ...projects.flatMap(({ item }) => {
      const keywords = `${item.ref} ${item.title}`
      return [
        { id: `project:${item.id}`, label: `Open project ${projectName(item)}`, keywords, href: projectHref(item.id), action: null },
        { id: `backlog:${item.id}`, label: `Open ${projectName(item)} backlog`, keywords, href: backlogHref(item.id), action: null },
      ]
    }),
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

/** Where Enter on an entry goes; `null` for the theme, which navigates nowhere. */
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
