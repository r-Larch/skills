// claude.mjs — find the claude executable and run it with an argv array, never through a shell.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { Refusal } from './launch-args.mjs'

/**
 * `CLAUDE_BIN` (a JSON array, for tests) or a PATH lookup. On Windows only claude.exe is accepted:
 * Node refuses to spawn a .cmd without a shell, and cmd.exe's quoting would mangle the brief line.
 * @param {NodeJS.ProcessEnv} [env]
 * @returns {string[]} the command and its leading arguments
 */
export function resolveClaude(env = process.env) {
    if (env.CLAUDE_BIN) {
        let v
        try {
            v = JSON.parse(env.CLAUDE_BIN)
        } catch {
            throw new Refusal('CLAUDE_BIN must be a JSON array, e.g. ["node","fake-claude.mjs"]')
        }
        if (!Array.isArray(v) || v.length === 0 || !v.every((x) => typeof x === 'string')) {
            throw new Refusal('CLAUDE_BIN must be a non-empty JSON array of strings')
        }
        return v
    }

    const dirs = (env.PATH ?? env.Path ?? '').split(path.delimiter).filter(Boolean)
    const home = (process.platform === 'win32' ? env.USERPROFILE : env.HOME) || os.homedir()
    if (process.platform === 'win32') {
        const exe = findIn([...dirs, path.join(home, '.local', 'bin')], 'claude.exe')
        if (exe) return [exe]
        const shim = findIn(dirs, 'claude.cmd') ?? findIn(dirs, 'claude.bat')
        if (shim) {
            throw new Refusal(`only a ${path.basename(shim)} shim was found (${shim}). The agents server runs claude without a shell, and a .cmd cannot be run that way without cmd.exe re-quoting the prompt. Install the native claude.exe (claude install), or set CLAUDE_BIN to a JSON array naming it.`)
        }
    } else {
        const bin = findIn([...dirs, path.join(home, '.local', 'bin')], 'claude')
        if (bin) return [bin]
    }
    throw new Refusal('claude is not on PATH. Set CLAUDE_BIN to a JSON array naming the executable.')
}

/** @param {string[]} dirs @param {string} file */
function findIn(dirs, file) {
    for (const d of dirs) {
        const p = path.join(d, file)
        try {
            if (fs.statSync(p).isFile()) return p
        } catch {}
    }
    return undefined
}

/**
 * @typedef {{code: number|null, stdout: string, stderr: string, error?: string}} RunResult
 */

/**
 * Run claude and collect its output. `claude --bg` exits once the session is registered, but the
 * session it started may keep the pipes open, so 'exit' plus a short drain ends the wait, not 'close'.
 * @param {string[]} args
 * @param {{cwd?: string, timeoutMs?: number, env?: NodeJS.ProcessEnv}} [opts]
 * @returns {Promise<RunResult>}
 */
export function runClaude(args, opts = {}) {
    const [cmd, ...pre] = resolveClaude(opts.env)
    return new Promise((resolve) => {
        let stdout = ''
        let stderr = ''
        let settled = false
        /** @param {RunResult} r */
        const done = (r) => {
            if (settled) return
            settled = true
            clearTimeout(timer)
            resolve(r)
        }
        const child = spawn(cmd, [...pre, ...args], {
            cwd: opts.cwd,
            env: opts.env ?? process.env,
            stdio: ['ignore', 'pipe', 'pipe'],
            windowsHide: true,
            shell: false,
        })
        child.stdout.setEncoding('utf8').on('data', (d) => (stdout += d))
        child.stderr.setEncoding('utf8').on('data', (d) => (stderr += d))
        const timer = setTimeout(() => {
            child.kill()
            done({ code: null, stdout, stderr, error: `claude ${args[0] ?? ''} did not exit within ${opts.timeoutMs ?? 60000} ms` })
        }, opts.timeoutMs ?? 60000)
        child.on('error', (e) => done({ code: null, stdout, stderr, error: e.message }))
        child.on('close', (code) => done({ code, stdout, stderr }))
        child.on('exit', (code) => {
            setTimeout(() => {
                child.stdout.destroy()
                child.stderr.destroy()
                done({ code, stdout, stderr })
            }, 500)
        })
    })
}

/** @param {string} s */
export function stripAnsi(s) {
    return s.replace(/\x1b\[[0-9;?]*[A-Za-z]/g, '')
}

/**
 * The short id from "backgrounded · <id> · <name>", colour codes and all.
 * @param {string} output
 */
export function parseBackgroundId(output) {
    const m = /backgrounded\s*[·|]\s*(\S+)/.exec(stripAnsi(output))
    return m ? m[1] : undefined
}

/**
 * @typedef {{id?: string, name?: string, status?: string, state?: string, waitingFor?: string, sessionId?: string, cwd?: string, pid?: number}} LiveAgent
 */

/**
 * `claude agents --json`, tolerant of a bare array or an object wrapping one.
 * @param {{env?: NodeJS.ProcessEnv}} [opts]
 * @returns {Promise<{agents: LiveAgent[], error?: string}>}
 */
export async function listLiveAgents(opts = {}) {
    let r
    try {
        r = await runClaude(['agents', '--json'], { timeoutMs: 15000, env: opts.env })
    } catch (e) {
        return { agents: [], error: /** @type {Error} */ (e).message }
    }
    if (r.error || r.code !== 0) return { agents: [], error: r.error ?? `claude agents --json exited ${r.code}: ${stripAnsi(r.stderr).trim()}` }
    try {
        const v = JSON.parse(r.stdout)
        if (Array.isArray(v)) return { agents: v }
        for (const k of ['agents', 'sessions', 'items']) if (Array.isArray(v?.[k])) return { agents: v[k] }
        return { agents: [], error: 'claude agents --json printed no list' }
    } catch {
        return { agents: [], error: 'claude agents --json did not print JSON' }
    }
}
