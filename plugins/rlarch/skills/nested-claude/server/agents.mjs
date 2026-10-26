#!/usr/bin/env node
// agents.mjs — the plugin's "agents" MCP server: newline-delimited JSON-RPC 2.0 on stdio.
//
// stdout carries protocol messages only; diagnostics go to stderr. Claude Code may probe the
// stateless 2026-07-28 revision with `server/discover` first; that is answered -32601 like any
// unknown method, and the client falls back to initialize.
// @ts-check

import readline from 'node:readline'
import { TOOLS, callTool, log } from './tools.mjs'

const SERVER_VERSION = '0.1.0'
const LATEST = '2025-11-25'
const SUPPORTED = [LATEST, '2025-06-18', '2025-03-26', '2024-11-05']

/** @param {unknown} msg */
function send(msg) {
    process.stdout.write(JSON.stringify(msg) + '\n')
}

/**
 * @param {string|number|null} id
 * @param {number} code
 * @param {string} message
 */
function sendError(id, code, message) {
    send({ jsonrpc: '2.0', id, error: { code, message } })
}

/**
 * @param {string} method
 * @param {any} params
 */
async function dispatch(method, params) {
    switch (method) {
        case 'initialize': {
            const asked = params?.protocolVersion
            return {
                protocolVersion: SUPPORTED.includes(asked) ? asked : LATEST,
                capabilities: { tools: { listChanged: false } },
                serverInfo: { name: 'agents', version: SERVER_VERSION },
                instructions: 'Launch, list and stop nested Claude Code background sessions. See the rlarch nested-claude skill.',
            }
        }
        case 'ping':
            return {}
        case 'tools/list':
            return { tools: TOOLS }
        case 'tools/call': {
            const name = params?.name
            if (!TOOLS.some((t) => t.name === name)) throw Object.assign(new Error(`unknown tool: ${name}`), { rpcCode: -32602 })
            const r = await callTool(name, params?.arguments)
            return { content: [{ type: 'text', text: r.text }], structuredContent: r.structured, isError: r.isError === true }
        }
        default:
            throw Object.assign(new Error(`method not found: ${method}`), { rpcCode: -32601 })
    }
}

/** @param {string} line */
async function handle(line) {
    if (!line.trim()) return
    let msg
    try {
        msg = JSON.parse(line)
    } catch {
        sendError(null, -32700, 'parse error')
        return
    }
    if (typeof msg !== 'object' || msg === null || typeof msg.method !== 'string') {
        if (msg && typeof msg === 'object' && 'id' in msg && !('method' in msg)) return
        sendError(msg?.id ?? null, -32600, 'invalid request')
        return
    }
    const isNotification = !('id' in msg) || msg.id === undefined
    if (isNotification) return
    try {
        send({ jsonrpc: '2.0', id: msg.id, result: await dispatch(msg.method, msg.params) })
    } catch (e) {
        const err = /** @type {Error & {rpcCode?: number}} */ (e)
        if (!err.rpcCode) log(`${msg.method} failed: ${err.message}`)
        sendError(msg.id, err.rpcCode ?? -32603, err.message)
    }
}

/** @type {Set<Promise<void>>} */
const pending = new Set()
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity })
rl.on('line', (line) => {
    const p = handle(line).finally(() => pending.delete(p))
    pending.add(p)
})
rl.on('close', async () => {
    await Promise.allSettled([...pending])
    process.exit(0)
})
