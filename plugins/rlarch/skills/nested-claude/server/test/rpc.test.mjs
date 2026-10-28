// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { startServer } from './helpers.mjs'

test('initialize echoes a supported protocol version and falls back to 2025-11-25', async () => {
    const s = startServer()
    try {
        const a = await s.request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 't', version: '0' } })
        assert.equal(a.result.protocolVersion, '2025-06-18')
        assert.deepEqual(a.result.capabilities, { tools: { listChanged: false } })
        assert.equal(a.result.serverInfo.name, 'agents')
        const b = await s.request('initialize', { protocolVersion: '2099-01-01' })
        assert.equal(b.result.protocolVersion, '2025-11-25')
    } finally {
        await s.close()
    }
})

test('notifications get no reply; ping answers {}', async () => {
    const s = startServer()
    try {
        s.notify('notifications/initialized')
        const p = await s.request('ping')
        assert.equal(p.id, 1)
        assert.deepEqual(p.result, {})
        assert.equal(s.output().stdout.trim().split('\n').length, 1)
    } finally {
        await s.close()
    }
})

test('tools/list: five tools, each an object schema; enum params carry enums', async () => {
    const s = startServer()
    try {
        const r = await s.request('tools/list')
        const tools = r.result.tools
        assert.deepEqual(tools.map((/** @type {any} */ t) => t.name), ['agent_launch', 'agent_stop', 'agent_list', 'agent_signin', 'agent_signout'])
        for (const t of tools) {
            assert.equal(typeof t.description, 'string')
            assert.equal(t.inputSchema.type, 'object')
        }
        const props = tools[0].inputSchema.properties
        assert.deepEqual(tools[0].inputSchema.required, ['name', 'brief', 'repo_root', 'controller'])
        assert.deepEqual(props.permission_mode.enum, ['acceptEdits', 'auto', 'bypassPermissions', 'manual', 'dontAsk', 'plan'])
        assert.deepEqual(props.toolset.enum, ['none', 'readonly', 'fix'])
        assert.deepEqual(props.effort.enum, ['low', 'medium', 'high', 'xhigh', 'max'])
        assert.deepEqual(props.workstate.properties.role.enum, ['worker', 'orchestrator', 'controller', 'none'])
    } finally {
        await s.close()
    }
})

test('unknown method and server/discover are -32601', async () => {
    const s = startServer()
    try {
        const d = await s.request('server/discover', {})
        assert.equal(d.error.code, -32601)
        assert.equal(d.result, undefined)
        const u = await s.request('no/such/method')
        assert.equal(u.error.code, -32601)
        const after = await s.request('initialize', { protocolVersion: '2025-11-25' })
        assert.equal(after.result.protocolVersion, '2025-11-25')
    } finally {
        await s.close()
    }
})

test('tools/call of an unknown tool is a -32602 protocol error', async () => {
    const s = startServer()
    try {
        const r = await s.request('tools/call', { name: 'agent_fly', arguments: {} })
        assert.equal(r.error.code, -32602)
    } finally {
        await s.close()
    }
})
