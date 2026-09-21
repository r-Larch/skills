/**
 * The watch floor's three states: unknown, empty, populated.
 *
 * Caught in a screenshot of a first paint — "Honesty across 8 runs" beside "Runs 0 / No run has been
 * imported yet." Every panel used `value?.length ?? 0`, which collapses *not asked yet* into *asked,
 * and the answer is none*. The one that matters is "Nothing needs you.": doc 09 §1 says this view
 * exists to answer "does anything need me right now?", and for the width of a request it was
 * answering **no** before it had asked. A supervision console may say no only when it knows.
 *
 * `src/fill.ts` and `src/queue.ts` own that decision, and the panel text lives beside it, so both the
 * state and the string it produces are checked here rather than described.
 *
 *   node verify/empty-states.mjs [baseUrl]
 */
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { createEffect, createRoot, createSignal } from 'solid-js/dist/solid.js'
import { UNKNOWN, count, countOf, fill } from '../src/fill.ts'
import { honestyCells } from '../src/queue.ts'
import { read as fetchJson, signIn } from './session.mjs'

const base = process.argv[2] ?? 'http://localhost:5290'

let failures = 0

const check = (label, actual, expected) => {
  const ok = JSON.stringify(actual) === JSON.stringify(expected)
  if (!ok) failures += 1
  console.log(
    `   ${ok ? 'PASS' : 'FAIL'} ${label}: ${JSON.stringify(actual)}` +
      (ok ? '' : ` (expected ${JSON.stringify(expected)})`),
  )
}

/**
 * What a panel puts on screen, from the same function the JSX switches on. `Filled` renders exactly
 * one of these three, so a state that never reaches the `empty` branch cannot print the empty text.
 */
const panel = (value, emptyText) => {
  const state = fill(value)
  if (state === 'unknown') return UNKNOWN
  if (state === 'empty') return emptyText
  return `${value.length} row(s)`
}

const NEEDS_YOU = 'Nothing needs you.'
const NO_RUNS = 'No run has been imported yet.'
const NO_CLAIM = 'No claim is held right now. Nothing is being worked, or nothing has said so.'

console.log('── the first frame: nothing has answered yet ───────────────────────────────')
check('needs you', panel(undefined, NEEDS_YOU), UNKNOWN)
check('runs', panel(undefined, NO_RUNS), UNKNOWN)
check('working now', panel(undefined, NO_CLAIM), UNKNOWN)
check('"Nothing needs you." is absent', panel(undefined, NEEDS_YOU) !== NEEDS_YOU, true)
check('"No run has been imported yet." is absent', panel(undefined, NO_RUNS) !== NO_RUNS, true)
check('section counts', [count(undefined), countOf(undefined, 'open')], [UNKNOWN, `${UNKNOWN} open`])
check(
  'honesty strip shows no number',
  honestyCells(undefined).map((c) => c.n),
  [UNKNOWN, UNKNOWN, UNKNOWN, UNKNOWN],
)
check(
  'honesty strip claims no hue',
  honestyCells(undefined).map((c) => c.tone),
  ['', '', '', ''],
)

console.log('\n── answered, and the answer is none ────────────────────────────────────────')
check('needs you', panel([], NEEDS_YOU), NEEDS_YOU)
check('runs', panel([], NO_RUNS), NO_RUNS)
check('working now', panel([], NO_CLAIM), NO_CLAIM)
check('section counts', [count([]), countOf([], 'open')], ['0', '0 open'])
check(
  'an honestly clean board still shows zeroes, not dashes',
  honestyCells({ doneUnverified: 0, forced: 0, ungated: 0, followupsOpen: 0 }).map((c) => c.n),
  ['0', '0', '0', '0'],
)

console.log('\n── answered, with rows: the live board ─────────────────────────────────────')
const cookie = await signIn(base)
const watch = await fetchJson(base, cookie, '/api/watch')
const projects = await fetchJson(base, cookie, '/api/projects')

check('needs you', panel(watch.needsYou, NEEDS_YOU), `${watch.needsYou.length} row(s)`)
check('working now', panel(watch.workingNow, NO_CLAIM), `${watch.workingNow.length} row(s)`)
check('projects', panel(projects, 'no projects'), `${projects.length} row(s)`)
check(
  'honesty strip, once known',
  honestyCells(watch.honesty).map((c) => c.n),
  [
    String(watch.honesty.doneUnverified),
    String(watch.honesty.forced),
    String(watch.honesty.ungated),
    String(watch.honesty.followupsOpen),
  ],
)

// The screenshot that started this: the runs panel resolving after the honesty strip.
console.log('\n── the interleaving from the screenshot ────────────────────────────────────')
console.log('   honesty answers first, runs is still in flight:')
console.log(`      Honesty   across ${UNKNOWN} runs`)
console.log(`      Runs  ${count(undefined)}`)
console.log(`      ${panel(undefined, NO_RUNS)}`)
check(
  'the contradiction cannot be printed',
  `Runs ${count(undefined)} ${panel(undefined, NO_RUNS)}`.includes(NO_RUNS),
  false,
)

/*
 * The first frame, as a sequence rather than a claim.
 *
 * There is no DOM here, but the panel's state is a derivation over signals, so the real reactive
 * core can be driven with a deliberately late answer and every value the derivation takes recorded
 * in order. That is what "the first frame" means: the first entry in this list is what is on screen
 * before the request returns.
 */
console.log('\n── the state sequence over a late answer ───────────────────────────────────')
const timeline = await new Promise((resolve) => {
  createRoot((dispose) => {
    const [board, setBoard] = createSignal(undefined)
    const seen = []

    createEffect(() => seen.push(panel(board()?.needsYou, NEEDS_YOU)))

    setTimeout(() => {
      void (async () => {
        const late = await fetchJson(base, cookie, '/api/watch')
        setBoard(() => late)
        setTimeout(() => {
          dispose()
          resolve(seen)
        }, 0)
      })()
    }, 250)
  })
})

console.log(`   the panel went: ${timeline.map((t) => JSON.stringify(t)).join(' → ')}`)
check('the first frame', timeline[0], UNKNOWN)
check('"Nothing needs you." never appeared', timeline.includes(NEEDS_YOU), false)
check('it ended populated', timeline[timeline.length - 1].endsWith('row(s)'), true)

// The bug was a shape, so guard the shape: `?? 0` on a length is how "not asked yet" becomes "none",
// and each empty sentence must exist once, at the one call site that is behind the `empty` branch.
console.log('\n── the shape that caused it, kept out of the source ────────────────────────')
const source = readFileSync(fileURLToPath(new URL('../src/views/WatchFloor.tsx', import.meta.url)), 'utf8')

// Comments name the banned shape in order to explain it, so they are not part of what is scanned.
const code = source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|\s)\/\/[^\n]*/g, '')

check('no `?? 0` anywhere in WatchFloor.tsx', /\?\?\s*0\b/.test(code), false)
check('no `.length ?? ` anywhere', /\.length\s*\?\?/.test(code), false)
for (const [name, text] of [['needs you', NEEDS_YOU], ['runs', NO_RUNS], ['working now', NO_CLAIM]]) {
  check(`"${name}" empty text appears once`, source.split(text).length - 1, 1)
}
check('every list panel goes through fill()/Filled', /Filled|fill\(/.test(source), true)

console.log(
  failures === 0
    ? `\nAll checks passed. Unknown renders "${UNKNOWN}" and no empty-state sentence is reachable before an answer.`
    : `\n${failures} check(s) failed.`,
)

if (failures > 0) process.exitCode = 1
