/**
 * The state vocabulary, case by case, through the one function every view colours with
 * (`src/state.ts`), plus the plan filter and the link builder that sit on top of it. No server:
 * every input is written here, so a change to the precedence fails here before it reaches a page.
 *
 *   node verify/state-cases.mjs
 */
import { CLOSED, LEAF, independent, state, tally } from '../src/state.ts'
import { plan } from '../src/plan.ts'
import { boardHref, itemHref, routeReads } from '../src/nav.ts'

let failures = 0

const check = (label, actual, expected) => {
  const ok = JSON.stringify(actual) === JSON.stringify(expected)
  if (!ok) failures += 1
  console.log(`   ${ok ? 'PASS' : 'FAIL'} ${label.padEnd(58)} ${JSON.stringify(actual)}${ok ? '' : `  (expected ${JSON.stringify(expected)})`}`)
}

const input = (over) => ({
  kind: 'task',
  status: 'open',
  resolution: null,
  claimExpired: false,
  proof: false,
  waitingOn: [],
  ...over,
})

console.log('── state(): every case, in precedence order ─────────────────────────────────')
const cases = [
  ['superseded', input({ status: 'superseded' }), { k: 'gone', label: 'Superseded' }],
  ['cancelled', input({ status: 'cancelled' }), { k: 'gone', label: 'Cancelled' }],
  ['blocked', input({ status: 'blocked' }), { k: 'stop', label: 'Blocked' }],
  ['blocked beats an expired claim', input({ status: 'blocked', claimExpired: true }), { k: 'stop', label: 'Blocked' }],
  ['in_progress, claim expired', input({ status: 'in_progress', claimExpired: true }), { k: 'stop', label: 'Claim expired' }],
  ['open, claim expired', input({ claimExpired: true }), { k: 'stop', label: 'Claim expired' }],
  ['done, stale claim row ignored', input({ status: 'done', claimExpired: true, proof: true }), { k: 'proven', label: 'Verified' }],
  ['in_progress', input({ status: 'in_progress' }), { k: 'doing', label: 'In progress' }],
  ['review', input({ status: 'review' }), { k: 'doing', label: 'In review' }],
  ['done + obsolete (a deferral)', input({ status: 'done', resolution: 'obsolete', proof: true }), { k: 'gone', label: 'Moved' }],
  ['done container (phase)', input({ kind: 'phase', status: 'done' }), { k: 'proven', label: 'Done' }],
  ['done container (run)', input({ kind: 'run', status: 'done' }), { k: 'proven', label: 'Done' }],
  ['done leaf, independent proof', input({ status: 'done', proof: true }), { k: 'proven', label: 'Verified' }],
  ['done leaf, no independent proof', input({ status: 'done' }), { k: 'claimed', label: 'Done, not verified' }],
  ['done leaf, proof unknown (ItemRef only)', input({ status: 'done', proof: null }), { k: 'claimed', label: 'Done' }],
  ['open, waiting on an open blocker', input({ waitingOn: ['m7/2.1'] }), { k: 'wait', label: 'Waiting on m7/2.1' }],
  ['ready, waiting on two', input({ status: 'ready', waitingOn: ['a', 'b'] }), { k: 'wait', label: 'Waiting on a, b' }],
  ['in_progress beats waiting', input({ status: 'in_progress', waitingOn: ['a'] }), { k: 'doing', label: 'In progress' }],
  ['open', input({}), { k: 'idle', label: 'Open' }],
  ['ready', input({ status: 'ready' }), { k: 'idle', label: 'Open' }],
]
for (const [label, i, expected] of cases) check(label, state(i), expected)

console.log('\n── independent(): who may prove a leaf ──────────────────────────────────────')
check('passed, run by orchestrator', independent({ passed: true, runBy: 'orchestrator' }), true)
check('passed, run by ci', independent({ passed: true, runBy: 'ci' }), true)
check('passed, run by human', independent({ passed: true, runBy: 'human' }), true)
check('passed, run by the worker itself', independent({ passed: true, runBy: 'worker' }), false)
check('failed, run by orchestrator', independent({ passed: false, runBy: 'orchestrator' }), false)
check('no verification', independent(null), false)
check('leaf kinds', [...LEAF].sort(), ['followup', 'incident', 'question', 'spike', 'subtask', 'task'])
check('closed statuses', [...CLOSED].sort(), ['cancelled', 'done', 'superseded'])

// A board in the v1 shape: the run, a loose task, two phases. Pre-order, depth from the run.
let n = 0
const ref = (kind, status, title = kind) => ({ id: `id-${++n}`, ref: String(n), qualifiedRef: `r/${n}`, kind, title, status })
const row = (item, depth, over = {}) => ({
  item,
  parentId: null,
  depth,
  seq: '1',
  hasChildren: false,
  resolution: null,
  priority: 0,
  severity: null,
  criteria: { checked: 0, total: 0 },
  rollup: null,
  claim: null,
  commit: null,
  verification: null,
  blockedBy: [],
  flags: [],
  openedAt: '2026-10-01T00:00:00Z',
  startedAt: null,
  closedAt: null,
  ...over,
})
const proven = { passed: true, runBy: 'orchestrator', at: '2026-10-02T00:00:00Z' }
const rows = [
  row(ref('run', 'in_progress'), 0),
  row(ref('task', 'open', 'loose'), 1),
  row(ref('phase', 'done', 'P1'), 1),
  row(ref('task', 'done'), 2, { verification: proven }),
  row(ref('task', 'done'), 2, { verification: { ...proven, runBy: 'worker' } }),
  row(ref('task', 'done'), 2, { resolution: 'obsolete' }),
  row(ref('phase', 'in_progress', 'P2'), 1),
  row(ref('task', 'blocked'), 2),
  row(ref('subtask', 'in_progress'), 3, { claim: { expired: true } }),
  row(ref('task', 'open'), 2, { blockedBy: [ref('task', 'blocked')] }),
  row(ref('task', 'cancelled'), 2),
]
const board = { rows }

console.log('\n── tally(): the segmented bar ───────────────────────────────────────────────')
const t = tally(rows)
check('counts', { proven: t.proven, claimed: t.claimed, doing: t.doing, stop: t.stop, wait: t.wait, idle: t.idle, gone: t.gone }, {
  proven: 1, claimed: 1, doing: 0, stop: 2, wait: 1, idle: 1, gone: 2,
})
check('total excludes gone, done = proven + claimed', [t.total, t.done], [6, 2])
check('segments drawn', t.leaves.length, 6)

console.log('\n── plan(): the filters ──────────────────────────────────────────────────────')
const shape = (groups) => groups.map((g) => [g.head?.item.title ?? 'Tasks', g.rows.length, g.tally ? `${g.tally.done}/${g.tally.total}` : '-'])
check('All', shape(plan(board, 'all')), [['Tasks', 1, '-'], ['P1', 3, '2/2'], ['P2', 4, '0/3']])
check('Open', shape(plan(board, 'open')), [['Tasks', 1, '-'], ['P2', 3, '0/3']])
check('Needs attention (stop or claimed)', shape(plan(board, 'attention')), [['P1', 1, '2/2'], ['P2', 2, '0/3']])
check('indent under a phase', plan(board, 'all')[2].rows.map((r) => r.indent), [0, 1, 0, 0])

console.log('\n── itemHref() / routeReads(): every link has a page ─────────────────────────')
const run = { id: 'R', kind: 'run' }
check('a run', itemHref(run, null), '/runs/R')
check('a task inside a run', itemHref({ id: 'T', kind: 'task' }, run), '/runs/R/T')
check('a task with no run known', itemHref({ id: 'T', kind: 'task' }, null), '/items/T')
check('a project, even with a run given', itemHref({ id: 'P', kind: 'project' }, run), '/items/P')
check('the run passed as its own run', itemHref(run, run), '/runs/R')
const onBoard = (id) => id === 'T'
check('run page: its own item', boardHref('R', onBoard, { id: 'T', kind: 'task' }), '/runs/R/T')
check('run page: the run itself', boardHref('R', onBoard, run), '/runs/R')
check('run page: a blocker in another run', boardHref('R', onBoard, { id: 'X', kind: 'task' }), '/items/X')
check('run page: another run', boardHref('R', onBoard, { id: 'S', kind: 'run' }), '/runs/S')
check('reads for /', routeReads('/'), ['/api/v1/overview'])
check('reads for /runs/R/T', routeReads('/runs/R/T?tab=evidence'), ['/api/v1/runs/R/board', '/api/v1/items/T'])
check('reads for /items/P', routeReads('/items/P'), ['/api/v1/items/P'])
check('reads for a backlog', routeReads('/projects/P/backlog'), ['/api/v1/backlog?project=P'])
check('an unknown path has no page', routeReads('/runs'), [])

console.log(failures === 0 ? '\nAll checks passed.' : `\n${failures} check(s) failed.`)
if (failures > 0) process.exitCode = 1
