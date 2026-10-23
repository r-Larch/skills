/**
 * No dead links: every ref link the UI can draw, built by the same `itemHref()` the views call, is
 * mapped by `routeReads()` to the API reads its page makes, and every one of those must answer 200.
 * The hrefs come from every surface that links: the sidebar and overview, the inbox, the activity
 * page, every board row, and every item page's crumbs, children, blockers and findings.
 *
 *   node verify/links.mjs [baseUrl]
 */
import { backlogHref, boardHref, itemHref, routeReads, runHref } from '../src/nav.ts'
import { DEFAULT_BASE, read as fetchJson, signIn, status } from './session.mjs'

const base = process.argv[2] ?? DEFAULT_BASE
const cookie = await signIn(base)
const read = (path) => fetchJson(base, cookie, path)

/** href → the surface that drew it, for the report. */
const hrefs = new Map()
const add = (href, from) => {
  if (!hrefs.has(href)) hrefs.set(href, from)
}

const inRun = (board, item) => boardHref(board.run.id, (id) => board.rows.some((r) => r.item.id === id), item)

for (const fixed of ['/', '/inbox', '/activity']) add(fixed, 'nav')

const overview = await read('/api/v1/overview')
const boards = []
for (const project of overview.projects) {
  add(backlogHref(project.item.id), 'overview: project panel')
  for (const run of project.runs) {
    add(runHref(run.item.id), 'sidebar / overview strip')
    boards.push(await read(`/api/v1/runs/${run.item.id}/board`))
  }
}
for (const claim of overview.workingNow) add(itemHref(claim.item, claim.run), 'overview: working now')

for (const row of (await read('/api/v1/inbox')).rows) add(itemHref(row.item, row.run), `inbox: ${row.kind}`)

for (const event of (await read('/api/v1/journal?limit=300')).events) {
  if (event.item) add(itemHref(event.item, event.run), `activity: ${event.kind}`)
}

for (const project of overview.projects) {
  const backlog = await read(`/api/v1/backlog?project=${project.item.id}`)
  for (const group of backlog.groups) {
    if (group.origin) add(itemHref(group.origin, null), 'backlog: origin')
    for (const item of group.items) add(itemHref(item, backlog.run), 'backlog: item')
  }
}

let pages = 0
for (const board of boards) {
  for (const row of board.rows) {
    add(inRun(board, row.item), 'run: plan list')
    for (const blocker of row.blockedBy) add(inRun(board, blocker), 'run: waiting on')

    const page = await read(`/api/v1/items/${row.item.id}`)
    pages += 1
    for (const step of page.path) add(inRun(board, step), 'detail: crumbs')
    for (const child of page.children) add(inRun(board, child.item), 'detail: inside')
    for (const finding of page.findings) add(inRun(board, finding.item), 'detail: findings')
    for (const link of page.links.in) add(inRun(board, link.item), 'detail: links')
  }
}

console.log(`── ${hrefs.size} distinct hrefs from ${boards.length} boards and ${pages} item pages ──────────────`)
const tally = new Map()
let dead = 0
for (const [href, from] of hrefs) {
  const reads = routeReads(href)
  if (reads.length === 0) {
    dead += 1
    console.log(`   DEAD ${href}  (${from}) — no route`)
    continue
  }
  for (const path of reads) {
    const code = await status(base, cookie, path)
    if (code !== 200) {
      dead += 1
      console.log(`   DEAD ${href}  (${from}) — GET ${path} → ${code}`)
    }
  }
  const kind = href.split('/')[1] || '(root)'
  tally.set(kind, (tally.get(kind) ?? 0) + 1)
}

console.log(`   by route: ${[...tally].map(([k, n]) => `/${k} ${n}`).join(' · ')}`)
console.log(dead === 0 ? `\nAll ${hrefs.size} links open a page that loads.` : `\n${dead} dead link(s).`)
if (dead > 0) process.exitCode = 1
