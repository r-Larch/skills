// workstate.mjs — mint, bind and revoke per-session WorkState tokens (docs/workstate/14 §2).
//
// The controller token is read from the env FILE by this process and goes nowhere but an
// Authorization header. A minted token goes nowhere but the mcp-<name>.json it is written to.
// Error messages are built from status codes and server error text, never from a header or body
// this code sent.
// @ts-check

import fs from 'node:fs'
import { Refusal } from './launch-args.mjs'

const TOKEN_KEYS = ['WORKSTATE_CONTROLLER_TOKEN', 'WORKSTATE_ORCHESTRATOR_TOKEN']

/**
 * KEY=VALUE lines; blank lines and # comments ignored; one layer of matching quotes stripped.
 * @param {string} text
 * @returns {Record<string, string>}
 */
export function parseEnvFile(text) {
    /** @type {Record<string, string>} */
    const out = {}
    for (const raw of text.replace(/^﻿/, '').split(/\r?\n/)) {
        const line = raw.trim()
        if (!line || line.startsWith('#')) continue
        const eq = line.indexOf('=')
        if (eq <= 0) continue
        const k = line.slice(0, eq).replace(/^export\s+/, '').trim()
        let v = line.slice(eq + 1).trim()
        if (v.length >= 2 && (v[0] === '"' || v[0] === "'") && v[v.length - 1] === v[0]) v = v.slice(1, -1)
        out[k] = v
    }
    return out
}

/**
 * @param {string} envFile
 * @returns {string} the controller token (orchestrator as fallback)
 */
export function readControllerToken(envFile) {
    let text
    try {
        text = fs.readFileSync(envFile, 'utf8')
    } catch (e) {
        throw new Refusal(`cannot read workstate.env_file ${envFile}: ${/** @type {NodeJS.ErrnoException} */ (e).code ?? 'unreadable'}`)
    }
    const env = parseEnvFile(text)
    for (const k of TOKEN_KEYS) if (env[k]) return env[k]
    throw new Refusal(`workstate.env_file ${envFile} has neither ${TOKEN_KEYS.join(' nor ')}; minting needs one of them`)
}

/**
 * @param {Response} res
 */
async function serverReason(res) {
    let detail = ''
    try {
        const body = await res.text()
        try {
            const j = JSON.parse(body)
            detail = j.error ?? j.title ?? j.message ?? j.detail ?? ''
        } catch {
            detail = body.slice(0, 200)
        }
    } catch {}
    return `HTTP ${res.status}${detail ? `: ${String(detail).slice(0, 300)}` : ''}`
}

/**
 * @param {string} url
 * @param {RequestInit} init
 */
async function call(url, init) {
    try {
        return await fetch(url, { ...init, signal: AbortSignal.timeout(15000) })
    } catch (e) {
        const err = /** @type {Error & {cause?: {code?: string}}} */ (e)
        throw new Error(`WorkState at ${new URL(url).origin} is unreachable (${err.cause?.code ?? err.name})`)
    }
}

/**
 * @typedef {{sessionId: string, role: string, token: string, expiresAt: string|undefined}} Minted
 */

/**
 * POST /api/agent-sessions. Throws Refusal with the reason on any failure.
 * @param {{url: string, controllerToken: string, name: string, role: string, launchedBy: string, cwd: string, ttlHours?: number, repo?: {remote?: string, root?: string}}} a
 * @returns {Promise<Minted>}
 */
export async function mintSession(a) {
    /** @type {Record<string, unknown>} */
    const body = { name: a.name, role: a.role, launchedBy: a.launchedBy, cwd: a.cwd }
    if (a.ttlHours !== undefined) body.ttlHours = a.ttlHours
    if (a.repo && (a.repo.remote || a.repo.root)) body.repo = a.repo
    let res
    try {
        res = await call(`${a.url}/api/agent-sessions`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${a.controllerToken}` },
            body: JSON.stringify(body),
        })
    } catch (e) {
        throw new Refusal(`minting a WorkState token failed: ${/** @type {Error} */ (e).message}. Nothing was launched.`)
    }
    if (!res.ok) {
        const hint = res.status === 409
            ? ` A live token already exists for "${a.name}": agent_stop that agent first, or pick another name.`
            : res.status === 401 || res.status === 403
                ? ' The env file token was refused, or it may not mint this role (a role is never raised).'
                : ''
        throw new Refusal(`minting a WorkState token failed: ${await serverReason(res)}.${hint} Nothing was launched.`)
    }
    let j
    try {
        j = await res.json()
    } catch {
        throw new Refusal('minting a WorkState token failed: the response was not JSON. Nothing was launched.')
    }
    if (!j || typeof j.token !== 'string' || !j.token || j.sessionId === undefined || j.sessionId === null) {
        throw new Refusal('minting a WorkState token failed: the response had no token or sessionId. Nothing was launched.')
    }
    return { sessionId: String(j.sessionId), role: String(j.role ?? a.role), token: j.token, expiresAt: j.expiresAt }
}

/**
 * PATCH the Claude session uuid onto the WorkState session. Best effort: returns an error string.
 * @param {{url: string, controllerToken: string, sessionId: string, claudeSessionId: string}} a
 * @returns {Promise<string|undefined>}
 */
export async function bindClaudeSession(a) {
    try {
        const res = await call(`${a.url}/api/agent-sessions/${encodeURIComponent(a.sessionId)}`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${a.controllerToken}` },
            body: JSON.stringify({ claudeSessionId: a.claudeSessionId }),
        })
        return res.ok ? undefined : await serverReason(res)
    } catch (e) {
        return /** @type {Error} */ (e).message
    }
}

/**
 * DELETE /api/agent-sessions/{id}. A 404 counts as revoked: there is nothing left to revoke.
 * @param {{url: string, controllerToken: string, sessionId: string}} a
 * @returns {Promise<string|undefined>} an error, or undefined when revoked
 */
export async function revokeSession(a) {
    try {
        const res = await call(`${a.url}/api/agent-sessions/${encodeURIComponent(a.sessionId)}`, {
            method: 'DELETE',
            headers: { Authorization: `Bearer ${a.controllerToken}` },
        })
        return res.ok || res.status === 404 ? undefined : await serverReason(res)
    } catch (e) {
        return /** @type {Error} */ (e).message
    }
}

/**
 * The --mcp-config file. A server named "workstate" here replaces the user-scope one, and a --bg
 * session gets no ${VAR} expansion, so the token value itself goes in (docs/workstate/12 §5).
 * @param {string} url
 * @param {string} token
 */
export function mcpConfigJson(url, token) {
    return JSON.stringify({ mcpServers: { workstate: { type: 'http', url: `${url}/mcp`, headers: { Authorization: `Bearer ${token}` } } } }, null, 2) + '\n'
}
