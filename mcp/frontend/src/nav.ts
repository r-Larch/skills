import type { ItemRef } from './api.ts'

/**
 * Every link to an item goes through here, so every link has a page behind it: a run opens its
 * board, anything inside a run opens the board with it selected, and anything else opens
 * `/items/{id}`, which moves itself onto the run page once it knows the run.
 */
export function itemHref(item: Pick<ItemRef, 'id' | 'kind'>, run: Pick<ItemRef, 'id'> | null | undefined): string {
  if (item.kind === 'run') return `/runs/${item.id}`
  if (item.kind !== 'project' && run != null && run.id !== item.id) return `/runs/${run.id}/${item.id}`
  return `/items/${item.id}`
}

export const runHref = (run: string) => `/runs/${run}`

/** What a copy button puts on the clipboard: the server's full path, never a ref composed here. */
export const copyValue = (item: Pick<ItemRef, 'fullRef'>): string => item.fullRef

/**
 * The full path as crumbs: each segment links to the item on `path` (or the item itself) it names,
 * and the last segment is the page itself, so it is text.
 */
export function pathCrumbs(item: ItemRef, path: ItemRef[]): { text: string; item: ItemRef | null }[] {
  const segments = item.fullRef.split('/')
  let from = 0
  return segments.map((text, i) => {
    if (i === segments.length - 1) return { text, item: null }
    const at = path.findIndex((p, j) => j >= from && p.ref.toLowerCase() === text.toLowerCase())
    if (at < 0) return { text, item: null }
    from = at + 1
    return { text, item: path[at]! }
  })
}

/** The query flag that shows cancelled work where it is hidden by default. */
export const SHOW_CANCELLED = 'show'

/**
 * A link drawn on a run page: inside this run's board it keeps the board and selects the item;
 * anything else (a blocker in another run, the project) leaves through `itemHref`.
 */
export function boardHref(run: string, onBoard: (id: string) => boolean, item: Pick<ItemRef, 'id' | 'kind'>): string {
  if (item.id === run) return runHref(run)
  if (onBoard(item.id)) return `${runHref(run)}/${item.id}`
  return itemHref(item, null)
}

export const backlogHref = (project: string) => `/projects/${project}/backlog`

export const projectHref = (project: string) => `/projects/${project}`

/** The run an item page's path places it in, or the item itself when it is one. */
export function runOf(item: ItemRef, path: ItemRef[]): ItemRef | null {
  if (item.kind === 'run') return item
  return [...path].reverse().find((p) => p.kind === 'run') ?? null
}

/**
 * The API reads a route's page makes, so `verify/links.mjs` can prove a link has a page behind it.
 * Empty means no route matches: the link would land on the not-found page.
 */
export function routeReads(href: string): string[] {
  const [path] = href.split('?')
  const parts = (path ?? '').split('/').filter(Boolean)

  if (parts.length === 0) return ['/api/v1/overview']
  if (parts[0] === 'inbox' && parts.length === 1) return ['/api/v1/inbox']
  if (parts[0] === 'activity' && parts.length === 1) return ['/api/v1/journal?limit=1']
  if (parts[0] === 'sessions' && parts.length === 1) return ['/api/v1/sessions']
  if (parts[0] === 'runs' && parts.length === 2) return [`/api/v1/runs/${parts[1]}/board`, `/api/v1/items/${parts[1]}`]
  if (parts[0] === 'runs' && parts.length === 3) return [`/api/v1/runs/${parts[1]}/board`, `/api/v1/items/${parts[2]}`]
  if (parts[0] === 'items' && parts.length === 2) return [`/api/v1/items/${parts[1]}`]
  if (parts[0] === 'projects' && parts.length === 2) return ['/api/v1/overview']
  if (parts[0] === 'projects' && parts[2] === 'backlog' && parts.length === 3) return [`/api/v1/backlog?project=${parts[1]}`]
  return []
}
