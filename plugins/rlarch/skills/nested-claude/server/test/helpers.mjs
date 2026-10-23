// helpers.mjs — a fake WorkState, a throwaway git repo, and a stdio client for agents.mjs.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import http from 'node:http'
import { spawn, execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
export const SERVER = path.join(here, '..', 'agents.mjs')
export const FAKE_CLAUDE = path.join(here, 'fake-claude.mjs')

export const MINTED_TOKEN = 'wss_FAKEminted0123456789abcdefMINTED'
export const CONTROLLER_TOKEN = 'ctl_FAKEcontroller0123456789SECRET'
export const CONTROLLER = 'skills-5d [e50df6]'

/** @param {string} prefix */
export function tmp(prefix) {
    return fs.mkdtempSync(path.join(os.tmpdir(), `agents-${prefix}-`))
}

/**
 * A git working tree with .claude/briefs/ ignored, a brief, and an env file outside it.
 * @param {{ignoreBriefs?: boolean}} [opts]
 */
export function makeRepo(opts = {}) {
    const root = tmp('repo')
    execFileSync('git', ['init', '-q'], { cwd: root })
    if (opts.ignoreBriefs !== false) fs.writeFileSync(path.join(root, '.gitignore'), '.claude/briefs/\n')
    fs.writeFileSync(path.join(root, 'task.md'), 'Do the unit.\n')
    const envDir = tmp('env')
    const envFile = path.join(envDir, '.env')
    fs.writeFileSync(envFile, [
        '# WorkState local secrets',
        '',
        'WORKSTATE_ORCHESTRATOR_TOKEN=orchestrator-not-this-one',
        `WORKSTATE_CONTROLLER_TOKEN="${CONTROLLER_TOKEN}"`,
        '',
    ].join('\n'))
    return { root, envFile, briefs: path.join(root, '.claude', 'briefs') }
}

/**
 * @typedef {{method: string, url: string, auth: string|undefined, body: any}} Recorded
 * @param {{mintStatus?: number}} [opts]
 */
export async function fakeWorkstate(opts = {}) {
    /** @type {Recorded[]} */
    const requests = []
    const server = http.createServer((req, res) => {
        let raw = ''
        req.on('data', (d) => (raw += d))
        req.on('end', () => {
            let body = null
            try { body = raw ? JSON.parse(raw) : null } catch { body = raw }
            requests.push({ method: req.method ?? '', url: req.url ?? '', auth: req.headers.authorization, body })
            const json = (/** @type {number} */ status, /** @type {unknown} */ v) => {
                res.writeHead(status, { 'Content-Type': 'application/json' })
                res.end(v === undefined ? '' : JSON.stringify(v))
            }
            if (req.method === 'POST' && req.url === '/api/agent-sessions') {
                if (opts.mintStatus && opts.mintStatus !== 201) return json(opts.mintStatus, { error: 'fake refusal' })
                return json(201, { sessionId: 'ws-42', name: body?.name, role: body?.role, token: MINTED_TOKEN, expiresAt: '2026-10-27T00:00:00Z' })
            }
            if (req.method === 'PATCH' && req.url === '/api/agent-sessions/ws-42') return json(200, { ok: true })
            if (req.method === 'DELETE' && req.url === '/api/agent-sessions/ws-42') {
                res.writeHead(204)
                return res.end()
            }
            json(404, { error: 'not found' })
        })
    })
    await new Promise((r) => server.listen(0, '127.0.0.1', () => r(undefined)))
    const addr = /** @type {import('node:net').AddressInfo} */ (server.address())
    return {
        url: `http://127.0.0.1:${addr.port}`,
        requests,
        close: () => new Promise((r) => server.close(() => r(undefined))),
    }
}

/**
 * Spawn agents.mjs with the fake claude and talk JSON-RPC to it.
 * @param {Record<string, string>} [env]
 */
export function startServer(env = {}) {
    const data = tmp('data')
    const claudeLog = path.join(data, 'claude-argv.jsonl')
    const child = spawn(process.execPath, [SERVER], {
        env: {
            ...process.env,
            CLAUDE_BIN: JSON.stringify([process.execPath, FAKE_CLAUDE]),
            FAKE_CLAUDE_LOG: claudeLog,
            CLAUDE_PLUGIN_DATA: data,
            AGENTS_SESSION_WAIT_MS: '1500',
            ...env,
        },
        stdio: ['pipe', 'pipe', 'pipe'],
    })
    let stdout = ''
    let stderr = ''
    let buf = ''
    /** @type {Map<number, (v: any) => void>} */
    const waiting = new Map()
    child.stdout.setEncoding('utf8').on('data', (d) => {
        stdout += d
        buf += d
        let i
        while ((i = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, i)
            buf = buf.slice(i + 1)
            const msg = JSON.parse(line)
            waiting.get(msg.id)?.(msg)
            waiting.delete(msg.id)
        }
    })
    child.stderr.setEncoding('utf8').on('data', (d) => (stderr += d))
    let next = 1

    /** @param {string} method @param {unknown} [params] @returns {Promise<any>} */
    function request(method, params) {
        const id = next++
        return new Promise((resolve) => {
            waiting.set(id, resolve)
            child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n')
        })
    }
    /** @param {string} method @param {unknown} [params] */
    function notify(method, params) {
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n')
    }
    /** @param {string} name @param {unknown} args */
    async function call(name, args) {
        const msg = await request('tools/call', { name, arguments: args })
        if (msg.error) throw new Error(`rpc error ${msg.error.code}: ${msg.error.message}`)
        return msg.result
    }
    /** @returns {{argv: string[], cwd: string}[]} */
    function claudeCalls() {
        if (!fs.existsSync(claudeLog)) return []
        return fs.readFileSync(claudeLog, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l))
    }
    async function close() {
        child.stdin.end()
        await new Promise((r) => child.on('exit', r))
    }
    return { request, notify, call, claudeCalls, close, data, output: () => ({ stdout, stderr }) }
}

/**
 * @param {string} dir
 * @returns {string[]} every file under dir
 */
export function walk(dir) {
    if (!fs.existsSync(dir)) return []
    /** @type {string[]} */
    const out = []
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name)
        if (e.isDirectory()) out.push(...walk(p))
        else out.push(p)
    }
    return out
}
