/**
 * The numbers on the dashboard, taken off the v1 endpoints the UI reads and put through the modules
 * the UI renders with: `src/state.ts` for every segment and dot, `src/inbox.ts` for the honesty
 * strip and the inbox summary. Nothing here restates a number; every line is what the code that
 * draws the pixel produced.
 *
 *   node verify/board-numbers.mjs [baseUrl] [runRef]
 */
import { rowState, tally } from '../src/state.ts'
import { groupInbox, honestyCells } from '../src/inbox.ts'
import { DEFAULT_BASE, read as fetchJson, signIn } from './session.mjs'

const base = process.argv[2] ?? DEFAULT_BASE
const focus = process.argv[3] ?? 'mature'

const cookie = await signIn(base)
const read = (path) => fetchJson(base, cookie, path)

const overview = await read('/api/v1/overview')
const KEYS = ['proven', 'claimed', 'doing', 'stop', 'wait', 'idle', 'gone']

console.log(`── segmented bars, as tally() counts them (asOf ${overview.asOf}, seq ${overview.seq}) ──────`)
console.log(`   ${'run'.padEnd(34)} ${KEYS.map((k) => k.padStart(7)).join('')}   done/total`)
const boards = new Map()
for (const project of overview.projects) {
  console.log(`   ${project.item.qualifiedRef}`)
  for (const run of project.runs) {
    const board = await read(`/api/v1/runs/${run.item.id}/board`)
    boards.set(run.item.id, board)
    const t = tally(board.rows)
    console.log(
      `     ${run.item.qualifiedRef.padEnd(32)} ${KEYS.map((k) => String(t[k]).padStart(7)).join('')}   ${t.done}/${t.total}` +
        `   (server rollup done ${run.rollup.done}/${run.rollup.total})`,
    )
  }
}

const target = [...boards.values()].find((b) => b.run.ref === focus || b.run.qualifiedRef === focus)
if (target) {
  console.log(`\n── ${target.run.qualifiedRef}, leaf by leaf ───────────────────────────────────────────────`)
  for (const row of target.rows) {
    const s = rowState(row)
    console.log(`   ${'  '.repeat(row.depth)}${row.item.ref.padEnd(8)} ${row.item.kind.padEnd(8)} ${row.item.status.padEnd(11)} → ${s.k.padEnd(7)} ${s.label}`)
  }
} else {
  console.log(`\n   (no run with ref "${focus}" — pass one as the second argument)`)
}

console.log('\n── honesty strip, as honestyCells() builds it ───────────────────────────────')
console.log('   raw honesty:', JSON.stringify(overview.honesty))
for (const cell of honestyCells(overview.honesty, overview.projects)) {
  console.log(`   ${String(cell.n).padStart(4)}  ${cell.bad ? 'red ' : '    '} ${cell.label}`)
}
const flagged = [...boards.values()].flatMap((b) => b.rows).filter((r) => r.flags.includes('done_unverified')).length
const claimed = [...boards.values()].reduce((n, b) => n + tally(b.rows).claimed, 0)
console.log(`   board flags done_unverified: ${flagged} · yellow segments (done leaf, no independent proof): ${claimed}`)

console.log('\n── inbox, as groupInbox() summarises it ─────────────────────────────────────')
const inbox = await read('/api/v1/inbox')
console.log(`   ${inbox.rows.length} rows (overview.inboxCount ${overview.inboxCount})`)
for (const group of groupInbox(inbox.rows)) {
  console.log(`   ${group.label.padEnd(18)} ${String(group.rows.length).padStart(4)}   e.g. ${group.rows[0].call}`)
}

console.log('\n── working now ──────────────────────────────────────────────────────────────')
if (overview.workingNow.length === 0) console.log('   no claim is held — "No agent holds a claim." renders')
for (const claim of overview.workingNow) {
  console.log(`   ${(claim.session?.name ?? claim.agent).padEnd(16)} ${claim.item.qualifiedRef.padEnd(20)} ${claim.expired ? 'EXPIRED' : 'live'} until ${claim.expiresAt}`)
}
