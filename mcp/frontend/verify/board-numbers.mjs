/**
 * The numbers on the board, taken off the endpoint the UI reads and put through the modules the UI
 * renders with — `src/queue.ts` for the honesty strip and the attention queue, `src/clock.ts` for the
 * lease countdown. Nothing here restates a number from a document; every line prints what the code
 * that draws the pixel actually produced.
 *
 *   node verify/board-numbers.mjs [baseUrl] [runRef]
 *
 * The credential is read by `verify/session.mjs`, never written here.
 */
import { PREVIEW, group, honestyCells } from '../src/queue.ts'
import { clockFace, remainingMs, skewFrom, staticFace } from '../src/clock.ts'
import { read as fetchJson, signIn } from './session.mjs'

const base = process.argv[2] ?? 'http://localhost:5290'
const runRef = process.argv[3] ?? 'entity-store'

const cookie = await signIn(base)
const read = (path) => fetchJson(base, cookie, path)

const watch = await read('/api/watch')

console.log('── honesty strip, as honestyCells() builds it ────────────────────────────────')
console.log('   raw honesty from /api/watch:', JSON.stringify(watch.honesty))
for (const cell of honestyCells(watch.honesty)) {
  console.log(`   ${String(cell.n).padStart(4)}  tone=${(cell.tone || 'none').padEnd(9)} ${cell.label}`)
}

console.log('\n── attention queue, as group() buckets it ───────────────────────────────────')
console.log(`   ${watch.needsYou.length} rows, ${watch.needsYou.filter((r) => r.qualifiedRef === null).length} without a qualifiedRef`)
const groups = group(watch.needsYou)
let shown = 0
for (const g of groups) {
  const first = Math.min(PREVIEW, g.rows.length)
  shown += first
  console.log(
    `   ${g.rank.padEnd(20)} ${String(g.rows.length).padStart(4)} rows · ${g.runs} run(s)` +
      ` · shows ${first} then "+${g.rows.length - first} more"`,
  )
}
console.log(`   first paint: ${groups.length} headings, ${shown} of ${watch.needsYou.length} rows visible`)

console.log('\n── lease clock, as clock.ts computes it ─────────────────────────────────────')
console.log(`   asOf ${watch.asOf} · skew vs this machine ${skewFrom(watch.asOf)} ms`)
if (watch.workingNow.length === 0) {
  console.log('   workingNow is empty — the deliberate empty state is what renders')
}
const skew = skewFrom(watch.asOf)
for (const lease of watch.workingNow) {
  const left = remainingMs(lease.expiresAt, Date.now(), skew)
  console.log(
    `   ${lease.agent} ${lease.qualifiedRef} expires ${lease.expiresAt}` +
      ` → ${clockFace(left)} (frozen: "${staticFace(left)}") ${left < 0 ? 'PAST ZERO, amber' : 'live, blue'}`,
  )
}

console.log('\n── the disagreement, as the tree row reads it ───────────────────────────────')
const tree = await read(`/api/runs/${encodeURIComponent(runRef)}/tree`)
const root = tree.nodes[0]
console.log(`   root ${root.ref}: ${root.doneCount}/${root.totalCount} done, ${root.reviewCount} in review`)
for (const node of tree.nodes.filter((n) => n.disagrees)) {
  console.log(`   ⚠ ${node.ref.padEnd(10)} ${node.status.padEnd(6)} ${node.doneCount}/${node.totalCount}`)
}
const marked = tree.nodes.filter((n) => n.disagrees).length
console.log(`   ${marked} of ${tree.nodes.length} nodes draw ⚠`)
