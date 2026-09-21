/**
 * The "since you last looked" divider, and the cursor key it depends on.
 *
 * This exists because the first version of the pane could never render the divider: it recomputed
 * the localStorage key from `props.run` inside `onCleanup`, which runs *after* the route has already
 * changed, so it wrote `workstate.seen.undefined` while `onMount` had read `workstate.seen.<run>`.
 * The read key and the write key never matched, `seen` stayed 0, and the line never appeared.
 *
 * Two parts, and neither is reasoning:
 *   1. `src/journal.ts` against the live journal — the key, and where the divider lands, including
 *      the exact case the controller asked for (an older stored seq) rendered as the row list the
 *      component's `<For>` emits.
 *   2. The defect itself, reproduced and then fixed, using the real solid-js reactive core: a props
 *      getter over a signal (what the JSX compiler emits for `run={params.run}`), flipped before the
 *      owner is disposed. The old shape writes `...undefined`; the new shape writes the run's key.
 *
 *   node verify/journal-divider.mjs [baseUrl] [runA] [runB]
 */
import { createRoot, createSignal, onCleanup } from 'solid-js/dist/solid.js'
import { dividerIndex, merge, newestFirst, newestSeq, seenKey } from '../src/journal.ts'
import { read as fetchJson, signIn } from './session.mjs'

const base = process.argv[2] ?? 'http://localhost:5290'
const runA = process.argv[3] ?? 'entity-store'
const runB = process.argv[4] ?? 'content-store'

let failures = 0

const check = (label, actual, expected) => {
  const ok = JSON.stringify(actual) === JSON.stringify(expected)
  if (!ok) failures += 1
  console.log(`   ${ok ? 'PASS' : 'FAIL'} ${label}: ${JSON.stringify(actual)}${ok ? '' : ` (expected ${JSON.stringify(expected)})`}`)
}

// ── 1. the key ───────────────────────────────────────────────────────────────────────────────────
console.log('── the localStorage key ─────────────────────────────────────────────────────')
check(`seenKey(${JSON.stringify(runA)})`, seenKey(runA), `workstate.seen.${runA}`)
check(`seenKey(${JSON.stringify(runB)})`, seenKey(runB), `workstate.seen.${runB}`)
check('the two runs do not share a key', seenKey(runA) !== seenKey(runB), true)

// ── 2. the divider, against the live journal ─────────────────────────────────────────────────────
const cookie = await signIn(base)
const page = await fetchJson(base, cookie, `/api/runs/${encodeURIComponent(runA)}/journal?limit=60`)
const rows = newestFirst(page.events)

console.log(`\n── the divider, over ${runA}'s ${rows.length} newest events ────────────────────────`)
console.log(`   seqs, newest first: ${rows.map((e) => e.seq).join(', ')}`)
console.log(`   newestSeq(): ${newestSeq(page.events)} — this is what leaving the run writes`)

check('never looked (seen = 0) → no divider', dividerIndex(rows, 0), undefined)
check('caught up (seen = newest) → no divider', dividerIndex(rows, rows[0].seq), undefined)
check('seen ahead of newest → no divider', dividerIndex(rows, rows[0].seq + 99), undefined)
check('no events at all → no divider', dividerIndex([], 42), undefined)

// The controller's case: put the cursor back a few events and the line must land between them.
const back = Math.min(3, rows.length - 1)
const stored = rows[back].seq
check(`stored seq set back to ${stored} → divider at index`, dividerIndex(rows, stored), back)

const older = rows[rows.length - 1].seq - 1
check(`stored seq older than everything loaded (${older}) → divider below all`, dividerIndex(rows, older), rows.length)

console.log(`\n   what the pane renders with workstate.seen.${runA} = ${stored}:`)
{
  const at = dividerIndex(rows, stored)
  rows.forEach((event, index) => {
    if (at === index) console.log('      ──── since you last looked ────────────────────')
    console.log(`      ${String(event.seq).padStart(4)}  ${event.ref ?? '—'}  ${(event.summary ?? event.kind).slice(0, 48)}`)
  })
  if (at === rows.length) console.log('      ──── since you last looked ────────────────────')
}

// ── 3. two runs, two journals ────────────────────────────────────────────────────────────────────
const pageB = await fetchJson(base, cookie, `/api/runs/${encodeURIComponent(runB)}/journal?limit=60`)
const seqsA = new Set(page.events.map((e) => e.seq))
const seqsB = new Set(pageB.events.map((e) => e.seq))

console.log('\n── two runs, two journals ───────────────────────────────────────────────────')
console.log(`   ${runA}: ${page.events.length} events, cursor ${page.cursor} · ${runB}: ${pageB.events.length} events, cursor ${pageB.cursor}`)
check('no seq appears in both runs', [...seqsA].some((s) => seqsB.has(s)), false)
check('merge() of one run into the other keeps them distinct', merge(page.events, pageB.events).length, page.events.length + pageB.events.length)

// ── 4. the reactive defect, reproduced and fixed, with real solid-js ─────────────────────────────
console.log('\n── the cleanup key, on the real reactive core ───────────────────────────────')

function writeOnDispose(build) {
  const store = {}
  const [routeRun, setRouteRun] = createSignal(runA)

  // What the JSX compiler emits for `run={params.run}`: a getter, live against the router's store.
  const props = { get run() { return routeRun() } }

  const dispose = createRoot((dispose) => {
    build(props, store)
    return dispose
  })

  // Leaving the run: the router clears the param, THEN the owner is disposed.
  setRouteRun(undefined)
  dispose()

  return store
}

const before = writeOnDispose((props, store) => {
  // The shipped-and-broken shape: the key is recomputed at cleanup time.
  onCleanup(() => {
    store[seenKey(props.run)] = '129'
  })
})

const after = writeOnDispose((props, store) => {
  // The fix: one string, read at creation, used for the read and the write.
  const key = seenKey(props.run)
  onCleanup(() => {
    store[key] = '129'
  })
})

check('old shape wrote', Object.keys(before), ['workstate.seen.undefined'])
check('new shape writes', Object.keys(after), [`workstate.seen.${runA}`])

console.log(
  failures === 0
    ? `\nAll checks passed. The key is "workstate.seen.${runA}" and the divider lands where it should.`
    : `\n${failures} check(s) failed.`,
)

if (failures > 0) process.exitCode = 1
