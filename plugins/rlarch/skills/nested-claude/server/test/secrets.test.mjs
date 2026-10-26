// No tool result, brief, state file or log line may contain a token. The fake tokens are distinctive
// strings, so a plain substring search over everything the server produced is the whole check.
// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { startServer, makeRepo, fakeWorkstate, walk, CONTROLLER, CONTROLLER_TOKEN, MINTED_TOKEN } from './helpers.mjs'

/** @param {string} where @param {string} text */
function assertClean(where, text) {
    assert.ok(!text.includes(MINTED_TOKEN), `minted token found in ${where}`)
    assert.ok(!text.includes(CONTROLLER_TOKEN), `controller token found in ${where}`)
}

test('launch, list, failed launch and stop leak no token anywhere but mcp-<name>.json', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    const failing = startServer({ FAKE_CLAUDE_BG_FAIL: '1' })
    try {
        const results = []
        const args = { brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, workstate: { env_file: repo.envFile, url: ws.url } }
        results.push(await s.call('agent_launch', { ...args, name: 'leak-1' }))
        results.push(await s.call('agent_launch', { ...args, name: 'leak-dry', dry_run: true }))
        results.push(await failing.call('agent_launch', { ...args, name: 'leak-fail' }))
        results.push(await s.call('agent_list', {}))

        const mcp = path.join(repo.briefs, 'mcp-leak-1.json')
        assert.ok(fs.readFileSync(mcp, 'utf8').includes(MINTED_TOKEN), 'the config is the one place the token belongs')

        for (const f of walk(s.data)) if (!f.endsWith('claude-argv.jsonl')) assertClean(f, fs.readFileSync(f, 'utf8'))
        assertClean('claude argv', fs.readFileSync(path.join(s.data, 'claude-argv.jsonl'), 'utf8'))
        for (const f of walk(repo.briefs)) if (f !== mcp) assertClean(f, fs.readFileSync(f, 'utf8'))

        results.push(await s.call('agent_stop', { name: 'leak-1', keep_brief: true }))
        for (const r of results) assertClean('a tool result', JSON.stringify(r))
        for (const f of walk(repo.briefs)) assertClean(f, fs.readFileSync(f, 'utf8'))
    } finally {
        await s.close()
        await failing.close()
        await ws.close()
    }
    for (const srv of [s, failing]) {
        const { stdout, stderr } = srv.output()
        assertClean('server stdout', stdout)
        assertClean('server stderr', stderr)
    }
})
