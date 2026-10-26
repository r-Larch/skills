// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { startServer, makeRepo, fakeWorkstate, CONTROLLER } from './helpers.mjs'

test('workstate.grants is passed to the mint as is; without it the mint body carries none', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        const granted = await s.call('agent_launch', {
            name: 'importer', brief: path.join(repo.root, 'task.md'), repo_root: repo.root, controller: CONTROLLER,
            workstate: { role: 'orchestrator', env_file: repo.envFile, url: ws.url, grants: ['import'] },
        })
        assert.equal(granted.isError, false, granted.content[0].text)

        const plain = await s.call('agent_launch', {
            name: 'plain', brief: path.join(repo.root, 'task.md'), repo_root: repo.root, controller: CONTROLLER,
            workstate: { role: 'orchestrator', env_file: repo.envFile, url: ws.url },
        })
        assert.equal(plain.isError, false, plain.content[0].text)

        const mints = ws.requests.filter((q) => q.method === 'POST')
        assert.deepEqual(mints.find((q) => q.body.name === 'importer')?.body.grants, ['import'])
        assert.equal('grants' in (mints.find((q) => q.body.name === 'plain')?.body ?? {}), false)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('workstate.grants: an unknown grant, or a grant with role none, is refused before anything is minted', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    const ok = { name: 'g', brief: path.join(repo.root, 'task.md'), repo_root: repo.root, controller: CONTROLLER }
    try {
        for (const [label, workstate, says] of /** @type {[string, object, RegExp][]} */ ([
            ['unknown grant', { role: 'orchestrator', env_file: repo.envFile, url: ws.url, grants: ['admin'] }, /workstate.grants must be an array of "import"/],
            ['not an array', { role: 'orchestrator', env_file: repo.envFile, url: ws.url, grants: 'import' }, /workstate.grants must be an array/],
            ['role none', { role: 'none', grants: ['import'] }, /mints none/],
        ])) {
            const r = await s.call('agent_launch', { ...ok, workstate })
            assert.equal(r.isError, true, label)
            assert.match(r.content[0].text, says, label)
        }
        assert.equal(ws.requests.length, 0)
        assert.equal(s.claudeCalls().length, 0)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a server refusal of the grant is relayed and nothing is launched', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate({ mintStatus: 400 })
    const s = startServer()
    try {
        const r = await s.call('agent_launch', {
            name: 'w', brief: path.join(repo.root, 'task.md'), repo_root: repo.root, controller: CONTROLLER,
            workstate: { role: 'worker', env_file: repo.envFile, url: ws.url, grants: ['import'] },
        })
        assert.equal(r.isError, true)
        assert.match(r.content[0].text, /Nothing was launched/)
        assert.equal(s.claudeCalls().length, 0)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('tools/list: workstate.grants is an array whose items carry the grant enum', async () => {
    const s = startServer()
    try {
        const r = await s.request('tools/list')
        const grants = r.result.tools[0].inputSchema.properties.workstate.properties.grants
        assert.equal(grants.type, 'array')
        assert.deepEqual(grants.items.enum, ['import'])
    } finally {
        await s.close()
    }
})
