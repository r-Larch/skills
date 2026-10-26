// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { startServer, makeRepo, fakeWorkstate, CONTROLLER, CONTROLLER_TOKEN, MINTED_TOKEN, tmp } from './helpers.mjs'
import { FIX_TOOLS, READONLY_TOOLS } from '../header.mjs'
import { WORKSTATE_TOOLS } from '../launch-args.mjs'

/** @param {ReturnType<typeof startServer>} s */
function bgArgv(s) {
    const calls = s.claudeCalls().filter((c) => c.argv.includes('--bg'))
    assert.equal(calls.length, 1, 'expected exactly one claude --bg')
    return calls[0]
}

/** @param {string} briefs @param {string} name */
const prompt = (briefs, name) => `Read the file ${path.join(briefs, `brief-${name}.txt`)} and follow it exactly. It is your brief. Start now.`

test('every parameter maps to its flag, variadics closed in order, prompt last', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const settings = path.join(repo.root, 'settings.json')
    fs.writeFileSync(settings, '{}')
    const s = startServer()
    try {
        const r = await s.call('agent_launch', {
            name: 'unit-1',
            brief: 'task.md',
            repo_root: repo.root,
            controller: CONTROLLER,
            permission_mode: 'acceptEdits',
            toolset: 'readonly',
            allowed_tools: ['Bash(node --test:*)', 'Read'],
            disallowed_tools: ['Bash(git push:*)', 'WebFetch'],
            add_dirs: ['P:\\packages\\nuget', '/opt/cache'],
            model: 'claude-opus-5-5',
            effort: 'high',
            settings: 'settings.json',
            append_system_prompt: 'Be brief.',
            workstate: { role: 'orchestrator', env_file: repo.envFile, url: ws.url, ttl_hours: 24 },
        })
        assert.equal(r.isError, false, r.content[0].text)
        const { argv, cwd } = bgArgv(s)
        const mcp = path.join(repo.briefs, 'mcp-unit-1.json')
        assert.deepEqual(argv, [
            '--bg', '--name', 'unit-1',
            '--allowedTools', ...READONLY_TOOLS, WORKSTATE_TOOLS, 'Bash(node --test:*)',
            '--disallowedTools', 'Bash(git push:*)', 'WebFetch',
            '--add-dir', 'P:\\packages\\nuget', '/opt/cache',
            '--mcp-config', mcp,
            '--model', 'claude-opus-5-5',
            '--effort', 'high',
            '--settings', settings,
            '--append-system-prompt', 'Be brief.',
            '--permission-mode', 'acceptEdits',
            prompt(repo.briefs, 'unit-1'),
        ])
        assert.equal(fs.realpathSync(cwd), fs.realpathSync(repo.root))

        const mint = ws.requests.find((q) => q.method === 'POST')
        assert.deepEqual(mint?.body, { name: 'unit-1', role: 'orchestrator', launchedBy: 'skills-5d', cwd: repo.root, ttlHours: 24 })
        assert.equal(mint?.auth, `Bearer ${CONTROLLER_TOKEN}`)

        assert.deepEqual(JSON.parse(fs.readFileSync(mcp, 'utf8')), {
            mcpServers: { workstate: { type: 'http', url: `${ws.url}/mcp`, headers: { Authorization: `Bearer ${MINTED_TOKEN}` } } },
        })
        if (process.platform !== 'win32') assert.equal(fs.statSync(mcp).mode & 0o777, 0o600)

        const patch = ws.requests.find((q) => q.method === 'PATCH')
        assert.equal(patch?.url, '/api/agent-sessions/ws-42')
        assert.deepEqual(patch?.body, { claudeSessionId: '11111111-2222-3333-4444-555555555555' })

        assert.equal(r.structuredContent.id, 'abc12345')
        assert.equal(r.structuredContent.sessionId, '11111111-2222-3333-4444-555555555555')
        assert.deepEqual(r.structuredContent.workstate, { session: 'ws-42', role: 'orchestrator', expiresAt: '2026-10-27T00:00:00Z' })

        const brief = fs.readFileSync(path.join(repo.briefs, 'brief-unit-1.txt'), 'utf8')
        assert.ok(brief.startsWith(`Your controller is the Claude Code session '${CONTROLLER}'.`))
        assert.ok(brief.endsWith('and wait.\nDo the unit.\n'))
    } finally {
        await s.close()
        await ws.close()
    }
})

test('defaults: auto, fix profile, worker role; --permission-mode closes the last variadic', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        const r = await s.call('agent_launch', {
            name: 'unit-2', brief: path.join(repo.root, 'task.md'), repo_root: repo.root, controller: CONTROLLER,
            workstate: { env_file: repo.envFile, url: ws.url },
        })
        assert.equal(r.isError, false, r.content[0].text)
        const { argv } = bgArgv(s)
        assert.deepEqual(argv, [
            '--bg', '--name', 'unit-2',
            '--allowedTools', ...FIX_TOOLS, WORKSTATE_TOOLS,
            '--mcp-config', path.join(repo.briefs, 'mcp-unit-2.json'),
            '--permission-mode', 'auto',
            prompt(repo.briefs, 'unit-2'),
        ])
        assert.equal(ws.requests.find((q) => q.method === 'POST')?.body.role, 'worker')
        assert.equal(r.structuredContent.toolset, 'fix')
        assert.equal(r.structuredContent.permission_mode, 'auto')
    } finally {
        await s.close()
        await ws.close()
    }
})

test('the token before the prompt is always a single-value flag', async () => {
    const repo = makeRepo()
    const s = startServer()
    try {
        await s.call('agent_launch', {
            name: 'unit-3', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER,
            add_dirs: ['/x'], workstate: { role: 'none' },
        })
        const { argv } = bgArgv(s)
        const variadic = ['--allowedTools', '--disallowedTools', '--add-dir', '--mcp-config']
        assert.equal(argv[argv.length - 3], '--permission-mode')
        const flags = argv.map((a, i) => [a, i]).filter(([a]) => String(a).startsWith('--'))
        const lastVariadic = Math.max(...flags.filter(([a]) => variadic.includes(String(a))).map(([, i]) => Number(i)))
        assert.ok(flags.some(([a, i]) => Number(i) > lastVariadic && !variadic.includes(String(a))), 'a variadic flag is not closed')
    } finally {
        await s.close()
    }
})

test('role none: no mint, no --mcp-config, no config file', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        const r = await s.call('agent_launch', {
            name: 'unit-4', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, toolset: 'none',
            workstate: { role: 'none', url: ws.url },
        })
        assert.equal(r.isError, false, r.content[0].text)
        const { argv } = bgArgv(s)
        assert.ok(!argv.includes('--mcp-config'))
        assert.ok(!argv.includes('--allowedTools'))
        assert.equal(ws.requests.length, 0)
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'mcp-unit-4.json')))
        assert.equal(r.structuredContent.workstate, null)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a minted session may call its WorkState tools without a prompt; without WorkState it may not', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        const minted = await s.call('agent_launch', {
            name: 'unit-ws', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, toolset: 'none',
            permission_mode: 'acceptEdits', workstate: { env_file: repo.envFile, url: ws.url },
        })
        assert.equal(minted.isError, false, minted.content[0].text)
        const plain = await s.call('agent_launch', {
            name: 'unit-plain', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, toolset: 'readonly',
            workstate: { role: 'none' },
        })
        assert.equal(plain.isError, false, plain.content[0].text)
        const calls = s.claudeCalls().filter((c) => c.argv.includes('--bg'))
        const allowed = (/** @type {string[]} */ argv) => argv.slice(argv.indexOf('--allowedTools') + 1, argv.findIndex((x, i) => i > argv.indexOf('--allowedTools') && x.startsWith('--')))
        assert.deepEqual(allowed(calls[0].argv), [WORKSTATE_TOOLS])
        assert.deepEqual(allowed(calls[1].argv), READONLY_TOOLS)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('mint failure (4xx, server down, no env file, no token key) refuses and spawns nothing', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate({ mintStatus: 403 })
    const noKey = path.join(tmp('env'), '.env')
    fs.writeFileSync(noKey, '# empty\nOTHER=1\n')
    const s = startServer()
    try {
        const base = { brief: 'task.md', repo_root: repo.root, controller: CONTROLLER }
        const cases = [
            { name: 'm-403', workstate: { env_file: repo.envFile, url: ws.url }, says: /HTTP 403/ },
            { name: 'm-down', workstate: { env_file: repo.envFile, url: 'http://127.0.0.1:9' }, says: /unreachable/ },
            { name: 'm-nofile', workstate: { env_file: path.join(repo.root, 'missing.env'), url: ws.url }, says: /cannot read workstate.env_file/ },
            { name: 'm-nokey', workstate: { env_file: noKey, url: ws.url }, says: /WORKSTATE_CONTROLLER_TOKEN/ },
            { name: 'm-noenv', workstate: { role: 'worker', url: ws.url }, says: /needs workstate.env_file/ },
        ]
        for (const c of cases) {
            const r = await s.call('agent_launch', { ...base, name: c.name, workstate: c.workstate })
            assert.equal(r.isError, true, c.name)
            assert.match(r.content[0].text, c.says, c.name)
            assert.ok(!fs.existsSync(path.join(repo.briefs, `mcp-${c.name}.json`)), c.name)
        }
        assert.equal(s.claudeCalls().length, 0, 'claude must not be run when minting fails')
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a failed claude --bg revokes the minted token and deletes its config', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer({ FAKE_CLAUDE_BG_FAIL: '1' })
    try {
        const r = await s.call('agent_launch', {
            name: 'unit-5', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER,
            workstate: { env_file: repo.envFile, url: ws.url },
        })
        assert.equal(r.isError, true)
        assert.match(r.content[0].text, /could not read the background id[\s\S]*fake launch failure[\s\S]*revoked/)
        assert.ok(ws.requests.some((q) => q.method === 'DELETE' && q.url === '/api/agent-sessions/ws-42'))
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'mcp-unit-5.json')))
    } finally {
        await s.close()
        await ws.close()
    }
})

test('validation refusals', async () => {
    const repo = makeRepo()
    const notGit = tmp('plain')
    fs.writeFileSync(path.join(notGit, 'task.md'), 'x')
    const outside = path.join(tmp('outside'), 'brief.md')
    fs.writeFileSync(outside, 'x')
    const unignored = makeRepo({ ignoreBriefs: false })
    const s = startServer()
    try {
        const ok = { name: 'v', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, workstate: { role: 'none' } }
        /** @type {[string, Record<string, unknown>, RegExp][]} */
        const cases = [
            ['missing name', { ...ok, name: undefined }, /name is required/],
            ['bad name', { ...ok, name: 'a b' }, /name is required/],
            ['relative repo_root', { ...ok, repo_root: 'repo' }, /absolute path/],
            ['missing repo_root', { ...ok, repo_root: path.join(repo.root, 'nope') }, /does not exist/],
            ['not a git tree', { ...ok, repo_root: notGit }, /not a git working tree/],
            ['controller without ref', { ...ok, controller: 'skills-5d' }, /must include the ref/],
            ['controller with short ref', { ...ok, controller: 'skills-5d [e50df]' }, /must include the ref/],
            ['missing brief', { ...ok, brief: 'nope.md' }, /brief not found/],
            ['brief outside repo_root', { ...ok, brief: outside }, /inside repo_root/],
            ['brief is repo_root', { ...ok, brief: '.' }, /brief not found/],
            ['bad permission_mode', { ...ok, permission_mode: 'yolo' }, /permission_mode must be one of/],
            ['bad toolset', { ...ok, toolset: 'all' }, /toolset must be one of/],
            ['bad effort', { ...ok, effort: 'extreme' }, /effort must be one of/],
            ['bad role', { ...ok, workstate: { role: 'admin' } }, /workstate.role must be one of/],
            ['flag-like add_dir', { ...ok, add_dirs: ['--evil'] }, /starts with "-"/],
            ['non-array allowed_tools', { ...ok, allowed_tools: 'Read' }, /must be an array/],
            ['bad settings JSON', { ...ok, settings: '{nope' }, /does not parse/],
            ['missing settings file', { ...ok, settings: 'nope.json' }, /settings file not found/],
            ['ttl out of range', { ...ok, workstate: { role: 'none', ttl_hours: 721 } }, /ttl_hours/],
            ['unknown parameter', { ...ok, permissionMode: 'auto' }, /unknown parameter/],
            ['config not gitignored', { ...ok, repo_root: unignored.root, workstate: { env_file: unignored.envFile } }, /not gitignored/],
        ]
        for (const [label, args, says] of cases) {
            const r = await s.call('agent_launch', args)
            assert.equal(r.isError, true, label)
            assert.match(r.content[0].text, says, label)
        }
        assert.equal(s.claudeCalls().length, 0)
    } finally {
        await s.close()
    }
})

test('dry_run: argv returned with the token as <minted>; nothing minted, spawned or recorded', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        const r = await s.call('agent_launch', {
            name: 'unit-6', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, dry_run: true,
            workstate: { env_file: repo.envFile, url: ws.url },
        })
        assert.equal(r.isError, false, r.content[0].text)
        const sc = r.structuredContent
        assert.equal(sc.dry_run, true)
        assert.equal(sc.argv[0], 'claude')
        assert.equal(sc.argv.at(-1), prompt(repo.briefs, 'unit-6'))
        assert.equal(sc.mcpConfig.content.mcpServers.workstate.headers.Authorization, 'Bearer <minted>')
        assert.match(r.content[0].text, /Bearer <minted>/)
        assert.equal(ws.requests.length, 0)
        assert.equal(s.claudeCalls().length, 0)
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'mcp-unit-6.json')))
        assert.ok(fs.existsSync(path.join(repo.briefs, 'brief-unit-6.txt')))
        const stateDir = path.join(s.data, 'agents')
        assert.deepEqual(fs.existsSync(stateDir) ? fs.readdirSync(stateDir) : [], [])
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a session id that never appears is a warning, not a failure', async () => {
    const repo = makeRepo()
    const ws = await fakeWorkstate()
    const s = startServer({ FAKE_CLAUDE_AGENTS: '[]', AGENTS_SESSION_WAIT_MS: '600' })
    try {
        const r = await s.call('agent_launch', {
            name: 'unit-8', brief: 'task.md', repo_root: repo.root, controller: CONTROLLER,
            workstate: { env_file: repo.envFile, url: ws.url },
        })
        assert.equal(r.isError, false)
        assert.equal(r.structuredContent.sessionId, null)
        assert.match(r.content[0].text, /warning: no sessionId yet/)
        assert.ok(!ws.requests.some((q) => q.method === 'PATCH'))
    } finally {
        await s.close()
        await ws.close()
    }
})
