// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import os from 'node:os'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { parsePsTable, nearestClaude, isClaude } from '../session-header.mjs'

const HELPER = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'session-header.mjs')

/** The measured Windows chain: node <- cmd.exe <- claude.exe (the session) <- claude.exe (supervisor). */
const CHAIN = [
    { pid: 70, ppid: 4, name: 'claude.exe', start: 1000 },
    { pid: 80, ppid: 70, name: 'claude.exe', start: 1100 },
    { pid: 90, ppid: 80, name: 'cmd.exe', start: 1200 },
    { pid: 100, ppid: 90, name: 'node.exe', start: 1201 },
    { pid: 4, ppid: 0, name: 'System', start: 0 },
]

/**
 * Run the helper as the harness does, with the process table and `claude agents` faked.
 * @param {object} table @param {unknown} agents @param {Record<string, string>} [extra]
 */
function helper(table, agents, extra = {}) {
    const out = execFileSync(process.execPath, [HELPER], {
        env: {
            ...process.env,
            WS_HELPER_PS_JSON: typeof table === 'string' ? table : JSON.stringify(table),
            WS_HELPER_AGENTS_JSON: JSON.stringify(agents),
            WS_HELPER_SELF_PID: '100',
            ...extra,
        },
        encoding: 'utf8',
    })
    return JSON.parse(out)
}

test('the nearest claude ancestor keys the session, and claude agents names it', () => {
    const headers = helper(CHAIN, [{ pid: 70, name: 'supervisor' }, { pid: 80, name: 'm27-probe' }])
    assert.deepEqual(headers, { 'X-WorkState-Client': `${os.hostname()}/80/1100`, 'X-WorkState-Agent': 'm27-probe' })
})

test('a session claude agents does not list (a fresh interactive one) sends the key alone', () => {
    assert.deepEqual(helper(CHAIN, { agents: [{ pid: 999, name: 'other' }] }), { 'X-WorkState-Client': `${os.hostname()}/80/1100` })
    assert.deepEqual(helper(CHAIN, [{ pid: 80, name: '' }]), { 'X-WorkState-Client': `${os.hostname()}/80/1100` })
})

test('CLAUDE_PID and CLAUDE_JOB_DIR are inherited from another session and never name this one', () => {
    const headers = helper(CHAIN, [{ pid: 70, name: 'someone-else' }], { CLAUDE_PID: '70', CLAUDE_JOB_DIR: 'x/jobs/someone-else' })
    assert.equal(headers['X-WorkState-Client'], `${os.hostname()}/80/1100`)
    assert.equal(headers['X-WorkState-Agent'], undefined)
})

test('no claude ancestor, a reused parent pid, or an unreadable table prints {}', () => {
    assert.deepEqual(helper(CHAIN.filter((p) => !p.name.startsWith('claude')), []), {})
    assert.deepEqual(helper(CHAIN.map((p) => (p.pid === 80 ? { ...p, start: 5000 } : p)), []), {})
    assert.deepEqual(helper('not json', []), {})
})

test('ps output parses, and a native claude or a node running Claude Code is claude', () => {
    const rows = parsePsTable([
        '  501     1 Mon Oct 26 15:40:30 2026 claude claude --name m27-probe',
        '  777   501 Mon Oct 26 15:40:31 2026 sh /bin/sh -c node /home/u/.workstate/session-header.mjs',
        '  778   777 Mon Oct 26 15:40:31 2026 node node /home/u/.workstate/session-header.mjs',
        '  900     1 Mon Oct 26 15:00:00 2026 node node /usr/lib/node_modules/@anthropic-ai/claude-code/cli.js',
    ].join('\n'))
    assert.equal(rows.length, 4)
    assert.equal(rows[0].start, Math.floor(new Date(2026, 9, 26, 15, 40, 30).getTime() / 1000))
    assert.equal(nearestClaude(rows, 778)?.pid, 501)
    assert.ok(isClaude(rows[3]))
    assert.ok(!isClaude(rows[2]))
})
