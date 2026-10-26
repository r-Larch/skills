#!/usr/bin/env node
// session-header.mjs — the WorkState registration's headersHelper. Prints ONE JSON object of headers:
//   X-WorkState-Client  "<host>/<pid>/<start epoch s>" of the nearest Claude Code ancestor process
//   X-WorkState-Agent   that process's name, only when `claude agents --json` lists its pid with one
// Anything that goes wrong prints {} and exits 0: a session without the headers is merely unnamed.
// Identity comes from the process table only. CLAUDE_PID and CLAUDE_JOB_DIR are inherited from
// whatever started the process and would name a session after another one (doc 11), so they are
// never read. No secret is read or printed; nothing but the JSON goes to stdout.
// WS_HELPER_PS_JSON, WS_HELPER_AGENTS_JSON and WS_HELPER_SELF_PID replace the two lookups and the
// starting pid in tests; WS_HELPER_DEBUG_FILE appends each result to a file.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const BUDGET_MS = 8000

/** @typedef {{pid: number, ppid: number, name: string, start: number, exe?: string, args?: string}} Proc */

/**
 * Whether a process table row is the Claude Code CLI. Windows: claude.exe. macOS/Linux: an
 * executable whose basename is `claude`, or a node running Claude Code's cli — assumed from the
 * install layouts (native binary, npm package), not measured on those platforms.
 * @param {Proc} p
 */
export function isClaude(p) {
    const name = path.basename(p.name ?? '').toLowerCase()
    if (name === 'claude.exe' || name === 'claude') return true
    const first = (p.args ?? '').trim().split(/\s+/)[0] ?? ''
    if (path.basename(first) === 'claude') return true
    return (name === 'node' || name === 'node.exe') && /claude-code[\\/].*cli\.(m?js)\b/.test(p.args ?? '')
}

/**
 * The nearest ancestor of `self` that is Claude Code. A parent that started after its child is a
 * reused pid, not the parent, and ends the walk.
 * @param {Proc[]} table
 * @param {number} self
 * @returns {Proc | undefined}
 */
export function nearestClaude(table, self) {
    const byPid = new Map(table.map((p) => [p.pid, p]))
    let child = byPid.get(self)
    for (let depth = 0; child && depth < 64; depth++) {
        const parent = byPid.get(child.ppid)
        if (!parent || parent.pid === child.pid || parent.start > child.start) return undefined
        if (isClaude(parent)) return parent
        child = parent
    }
    return undefined
}

/**
 * @param {Proc} claude
 * @param {string} host
 * @param {unknown} agents what `claude agents --json` printed, parsed
 * @returns {Record<string, string>}
 */
export function headersFor(claude, host, agents) {
    /** @type {Record<string, string>} */
    const headers = { 'X-WorkState-Client': `${host}/${claude.pid}/${claude.start}` }
    const list = Array.isArray(agents)
        ? agents
        : ['agents', 'sessions', 'items'].map((k) => /** @type {any} */ (agents)?.[k]).find(Array.isArray) ?? []
    const mine = list.find((a) => Number(a?.pid) === claude.pid)
    if (typeof mine?.name === 'string' && mine.name.trim()) headers['X-WorkState-Agent'] = mine.name.trim()
    return headers
}

/** @param {string} text */
export function parseWindowsTable(text) {
    const rows = JSON.parse(text)
    return (Array.isArray(rows) ? rows : [rows]).map((r) => ({
        pid: Number(r.p), ppid: Number(r.q), name: String(r.n ?? ''), start: Number(r.s ?? 0), exe: r.x ?? undefined,
    }))
}

/**
 * `ps -A -o pid=,ppid=,lstart=,comm=,args=` under LC_ALL=C: lstart is five fields
 * ("Mon Oct 26 15:40:34 2026"), comm one, args the rest.
 * @param {string} text
 */
export function parsePsTable(text) {
    /** @type {Proc[]} */
    const rows = []
    for (const line of text.split('\n')) {
        const f = line.trim().split(/\s+/)
        if (f.length < 8) continue
        const start = Math.floor(Date.parse(f.slice(3, 7).join(' ')) / 1000)
        rows.push({ pid: Number(f[0]), ppid: Number(f[1]), start: Number.isFinite(start) ? start : 0, name: f[7], args: f.slice(8).join(' ') })
    }
    return rows
}

const WINDOWS_TABLE = [
    'Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath |',
    'ForEach-Object { [pscustomobject]@{ p = $_.ProcessId; q = $_.ParentProcessId; n = $_.Name;',
    's = $(if ($_.CreationDate) { ([DateTimeOffset]$_.CreationDate).ToUnixTimeSeconds() } else { 0 });',
    'x = $_.ExecutablePath } } | ConvertTo-Json -Compress',
].join(' ')

/**
 * @param {string} cmd @param {string[]} args @param {number} timeoutMs @param {NodeJS.ProcessEnv} [env]
 * @returns {Promise<string>} stdout, or a rejection on error, non-zero exit or timeout
 */
function run(cmd, args, timeoutMs, env) {
    return new Promise((resolve, reject) => {
        let out = ''
        const child = spawn(cmd, args, { stdio: ['ignore', 'pipe', 'ignore'], windowsHide: true, shell: false, env: env ?? process.env })
        const timer = setTimeout(() => {
            child.kill()
            reject(new Error(`${cmd} timed out`))
        }, Math.max(timeoutMs, 1))
        child.stdout.setEncoding('utf8').on('data', (d) => (out += d))
        child.on('error', (e) => {
            clearTimeout(timer)
            reject(e)
        })
        child.on('close', (code) => {
            clearTimeout(timer)
            code === 0 ? resolve(out) : reject(new Error(`${cmd} exited ${code}`))
        })
    })
}

/** @param {number} deadline */
async function processTable(deadline) {
    if (process.env.WS_HELPER_PS_JSON) return /** @type {Proc[]} */ (JSON.parse(process.env.WS_HELPER_PS_JSON))
    if (process.platform === 'win32') {
        const encoded = Buffer.from(WINDOWS_TABLE, 'utf16le').toString('base64')
        return parseWindowsTable(await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], deadline - Date.now() - 1500))
    }
    const text = await run('ps', ['-A', '-o', 'pid=,ppid=,lstart=,comm=,args='], deadline - Date.now() - 1500, { ...process.env, LC_ALL: 'C' })
    return parsePsTable(text)
}

/** @param {Proc} claude @param {number} deadline */
async function liveAgents(claude, deadline) {
    if (process.env.WS_HELPER_AGENTS_JSON) return JSON.parse(process.env.WS_HELPER_AGENTS_JSON)
    const exe = claude.exe && fs.existsSync(claude.exe) ? claude.exe : process.platform === 'win32' ? 'claude.exe' : 'claude'
    return JSON.parse(await run(exe, ['agents', '--json'], deadline - Date.now() - 200))
}

/** @returns {Promise<Record<string, string>>} */
export async function sessionHeaders() {
    const deadline = Date.now() + BUDGET_MS
    const self = process.env.WS_HELPER_SELF_PID ? Number(process.env.WS_HELPER_SELF_PID) : process.pid
    const claude = nearestClaude(await processTable(deadline), self)
    if (!claude) return {}
    let agents = []
    try {
        agents = await liveAgents(claude, deadline)
    } catch {
        // The key alone still lets work_session bind a name.
    }
    return headersFor(claude, os.hostname(), agents)
}

/** @param {Record<string, string>} headers */
function emit(headers) {
    const line = JSON.stringify(headers)
    if (process.env.WS_HELPER_DEBUG_FILE) {
        try {
            fs.appendFileSync(process.env.WS_HELPER_DEBUG_FILE, `${new Date().toISOString()} ${line}\n`)
        } catch {}
    }
    process.stdout.write(line)
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const timer = setTimeout(() => {
        emit({})
        process.exit(0)
    }, BUDGET_MS)
    sessionHeaders()
        .catch(() => ({}))
        .then((headers) => {
            clearTimeout(timer)
            emit(headers)
            process.exit(0)
        })
}
