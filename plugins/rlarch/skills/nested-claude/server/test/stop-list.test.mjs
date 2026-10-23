// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { startServer, makeRepo, fakeWorkstate, CONTROLLER, CONTROLLER_TOKEN } from './helpers.mjs'

/** @param {ReturnType<typeof startServer>} s @param {string} url @param {ReturnType<typeof makeRepo>} repo @param {string} name */
async function launch(s, url, repo, name) {
    const r = await s.call('agent_launch', {
        name, brief: 'task.md', repo_root: repo.root, controller: CONTROLLER,
        workstate: { env_file: repo.envFile, url },
    })
    assert.equal(r.isError, false, r.content[0].text)
    return r.structuredContent
}

test('stop with keep_brief: claude stop, DELETE with the controller token, config deleted, brief kept', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        await launch(s, ws.url, repo, 'unit-s1')
        const mcp = path.join(repo.briefs, 'mcp-unit-s1.json')
        const brief = path.join(repo.briefs, 'brief-unit-s1.txt')
        assert.ok(fs.existsSync(mcp))

        const r = await s.call('agent_stop', { name: 'unit-s1', keep_brief: true })
        assert.equal(r.isError, false, r.content[0].text)
        assert.ok(s.claudeCalls().some((c) => c.argv[0] === 'stop' && c.argv[1] === 'abc12345'))
        assert.ok(!s.claudeCalls().some((c) => c.argv[0] === 'rm'))
        const del = ws.requests.find((q) => q.method === 'DELETE')
        assert.equal(del?.url, '/api/agent-sessions/ws-42')
        assert.equal(del?.auth, `Bearer ${CONTROLLER_TOKEN}`)
        assert.ok(!fs.existsSync(mcp), 'config must be deleted even with keep_brief')
        assert.ok(fs.existsSync(brief))
        assert.equal(r.structuredContent.revoked, true)
        assert.deepEqual(fs.readdirSync(path.join(s.data, 'agents')), [])
    } finally {
        await s.close()
        await ws.close()
    }
})

test('stop by id with remove: claude stop and rm, brief deleted', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        await launch(s, ws.url, repo, 'unit-s2')
        const r = await s.call('agent_stop', { id: 'abc12345', remove: true })
        assert.equal(r.isError, false, r.content[0].text)
        const argvs = s.claudeCalls().map((c) => c.argv.join(' '))
        assert.ok(argvs.includes('stop abc12345'))
        assert.ok(argvs.includes('rm abc12345'))
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'brief-unit-s2.txt')))
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'mcp-unit-s2.json')))
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a failed revoke is an error, and the record is kept for a retry; the config is still deleted', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        await launch(s, ws.url, repo, 'unit-s3')
        await ws.close()
        const r = await s.call('agent_stop', { name: 'unit-s3' })
        assert.equal(r.isError, true)
        assert.match(r.content[0].text, /could not revoke/)
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'mcp-unit-s3.json')))
        assert.deepEqual(fs.readdirSync(path.join(s.data, 'agents')), ['unit-s3.json'])
    } finally {
        await s.close()
    }
})

test('stop of an unknown name is refused', async () => {
    const s = startServer()
    try {
        const r = await s.call('agent_stop', { name: 'ghost' })
        assert.equal(r.isError, true)
        assert.match(r.content[0].text, /pass its id/)
        const n = await s.call('agent_stop', {})
        assert.match(n.content[0].text, /needs id or name/)
    } finally {
        await s.close()
    }
})

test('list joins claude agents --json with the launch records', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        await launch(s, ws.url, repo, 'unit-l1')
        const r = await s.call('agent_list', {})
        assert.equal(r.isError, false)
        const rows = r.structuredContent.agents
        const mine = rows.find((/** @type {any} */ x) => x.name === 'unit-l1')
        assert.deepEqual(mine, {
            name: 'unit-l1', id: 'abc12345', status: 'busy', state: 'running', waitingFor: null,
            sessionId: '11111111-2222-3333-4444-555555555555', repo_root: repo.root,
            workstate: { session: 'ws-42', role: 'worker', expiresAt: '2026-10-27T00:00:00Z' }, tracked: true,
        })
        const other = rows.find((/** @type {any} */ x) => x.id === 'ffff0000')
        assert.equal(other.tracked, false)
        assert.equal(other.waitingFor, 'permission prompt')
        assert.equal(other.workstate, null)
        assert.match(r.content[0].text, /unit-l1  abc12345  busy/)
    } finally {
        await s.close()
        await ws.close()
    }
})
