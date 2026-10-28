// oauth.mjs — sign in to a WorkState server (RFC 9728 discovery, RFC 7591 DCR, PKCE S256, RFC 8252
// loopback redirect), keep the grant in ~/.workstate/grants.json, and trade it for access tokens.
//
// A refresh token, access token or code verifier goes nowhere but grants.json, a request body or an
// Authorization header: never into a tool result, an error, a log line or mcp-<name>.json.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import http from 'node:http'
import crypto from 'node:crypto'
import { spawn } from 'node:child_process'
import { Refusal } from './launch-args.mjs'
import { call, serverReason } from './workstate.mjs'

export const SCOPE = 'workstate.sessions offline_access'
const SIGNIN_TIMEOUT_MS = 5 * 60 * 1000
const EXPIRY_SKEW_MS = 60 * 1000

/**
 * @typedef {{client_id: string, refresh_token?: string, scope?: string, obtained_at?: string}} Grant
 * @typedef {{resource: string, authorization_endpoint: string, token_endpoint: string, registration_endpoint?: string, revocation_endpoint?: string}} Metadata
 */

/** @param {NodeJS.ProcessEnv} [env] */
export function grantsFile(env = process.env) {
    return env.WORKSTATE_GRANTS_FILE || path.join(os.homedir(), '.workstate', 'grants.json')
}

/** @param {string} url */
export function originOf(url) {
    return new URL(url).origin
}

/** @param {string} url */
export function signinCall(url) {
    return `agent_signin {"url": "${url}"}`
}

/** @returns {Record<string, Grant>} */
function readGrants() {
    let text
    try {
        text = fs.readFileSync(grantsFile(), 'utf8')
    } catch (e) {
        if (/** @type {NodeJS.ErrnoException} */ (e).code === 'ENOENT') return {}
        throw new Refusal(`cannot read ${grantsFile()}: ${/** @type {NodeJS.ErrnoException} */ (e).code ?? 'unreadable'}`)
    }
    try {
        const j = JSON.parse(text)
        return j && typeof j === 'object' && !Array.isArray(j) ? j : {}
    } catch {
        throw new Refusal(`${grantsFile()} is not valid JSON; delete it and run agent_signin again`)
    }
}

/** @param {Record<string, Grant>} grants */
function writeGrants(grants) {
    const file = grantsFile()
    const tmp = `${file}.${process.pid}.${crypto.randomBytes(4).toString('hex')}.tmp`
    fs.writeFileSync(tmp, JSON.stringify(grants, null, 2) + '\n', { mode: 0o600 })
    try {
        fs.chmodSync(tmp, 0o600)
    } catch {}
    for (let i = 0; ; i++) {
        try {
            fs.renameSync(tmp, file)
            return
        } catch (e) {
            // Windows refuses the rename while another process has the target open.
            if (i >= 20 || /** @type {NodeJS.ErrnoException} */ (e).code !== 'EPERM') {
                fs.rmSync(tmp, { force: true })
                throw e
            }
            Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 50)
        }
    }
}

function ensureDir() {
    const dir = path.dirname(grantsFile())
    if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true, mode: 0o700 })
        try {
            fs.chmodSync(dir, 0o700)
        } catch {}
    }
}

/** @param {number} ms */
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

/**
 * Serialises every read-modify-write of grants.json across processes: a rotated refresh token
 * redeemed twice gets the whole authorization revoked by the server.
 * @template T
 * @param {() => Promise<T>} fn
 * @returns {Promise<T>}
 */
async function withGrantsLock(fn) {
    ensureDir()
    const lock = `${grantsFile()}.lock`
    const deadline = Date.now() + 20000
    for (;;) {
        try {
            fs.writeFileSync(lock, String(process.pid), { flag: 'wx' })
            break
        } catch (e) {
            if (/** @type {NodeJS.ErrnoException} */ (e).code !== 'EEXIST') throw e
            try {
                if (Date.now() - fs.statSync(lock).mtimeMs > 30000) {
                    fs.rmSync(lock, { force: true })
                    continue
                }
            } catch {}
            if (Date.now() > deadline) throw new Refusal(`${lock} is held by another process; retry, or delete it if no agents server is running`)
            await sleep(50)
        }
    }
    try {
        return await fn()
    } finally {
        fs.rmSync(lock, { force: true })
    }
}

/** @type {Map<string, Metadata>} */
const metadataCache = new Map()

/**
 * @param {string} url
 * @returns {Promise<any>}
 */
async function getJson(url) {
    const res = await call(url, { headers: { Accept: 'application/json' } })
    if (!res.ok) throw new Refusal(`${url} answered ${await serverReason(res)}: the server does not offer OAuth sign-in (WorkState 0.9.0 or later does), so pass workstate.env_file instead`)
    try {
        return await res.json()
    } catch {
        throw new Refusal(`${url} did not answer JSON`)
    }
}

/**
 * @param {string} url  the WorkState base url
 * @returns {Promise<Metadata>}
 */
async function discover(url) {
    const origin = originOf(url)
    const cached = metadataCache.get(origin)
    if (cached) return cached
    let prm, as
    try {
        prm = await getJson(`${url}/.well-known/oauth-protected-resource/api`)
        const issuer = String(prm.authorization_servers?.[0] ?? origin).replace(/\/+$/, '')
        as = await getJson(`${issuer}/.well-known/oauth-authorization-server`)
    } catch (e) {
        if (e instanceof Refusal) throw e
        throw new Refusal(/** @type {Error} */ (e).message)
    }
    if (typeof prm.resource !== 'string' || typeof as.authorization_endpoint !== 'string' || typeof as.token_endpoint !== 'string') {
        throw new Refusal(`the OAuth metadata of ${origin} has no resource, authorization_endpoint or token_endpoint`)
    }
    /** @type {Metadata} */
    const meta = {
        resource: prm.resource,
        authorization_endpoint: as.authorization_endpoint,
        token_endpoint: as.token_endpoint,
        registration_endpoint: as.registration_endpoint,
        revocation_endpoint: as.revocation_endpoint,
    }
    metadataCache.set(origin, meta)
    return meta
}

/**
 * @param {string} url
 * @param {Record<string, string>} params
 */
function postForm(url, params) {
    return call(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
        body: new URLSearchParams(params).toString(),
    })
}

/**
 * @param {Response} res
 * @returns {Promise<{error?: string, error_description?: string, [k: string]: unknown}>}
 */
async function bodyJson(res) {
    try {
        return await res.json()
    } catch {
        return {}
    }
}

/** @param {{error?: string, error_description?: string}} j @param {number} status */
const oauthReason = (j, status) => `HTTP ${status}${j.error ? `: ${j.error}` : ''}${j.error_description ? ` (${String(j.error_description).slice(0, 200)})` : ''}`

/**
 * @param {Metadata} meta
 * @param {string} origin
 * @returns {Promise<string>} a client_id, cached in grants.json per origin
 */
async function clientId(meta, origin) {
    const known = readGrants()[origin]?.client_id
    if (known) return known
    if (!meta.registration_endpoint) throw new Refusal(`${origin} offers no client registration (registration_endpoint)`)
    const res = await call(meta.registration_endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
        body: JSON.stringify({
            client_name: 'rlarch agents (agent_launch)',
            redirect_uris: ['http://127.0.0.1/callback'],
            grant_types: ['authorization_code', 'refresh_token'],
            response_types: ['code'],
            token_endpoint_auth_method: 'none',
            scope: SCOPE,
        }),
    })
    const j = await bodyJson(res)
    if (!res.ok || typeof j.client_id !== 'string') throw new Refusal(`registering a client at ${origin} failed: ${oauthReason(j, res.status)}`)
    const id = j.client_id
    await withGrantsLock(async () => {
        const grants = readGrants()
        grants[origin] = { ...grants[origin], client_id: id }
        writeGrants(grants)
    })
    return id
}

/** @param {Buffer} b */
const b64url = (b) => b.toString('base64url')

/**
 * The display name of the signed-in user when the access token is a readable JWT.
 * @param {string} accessToken
 */
function subjectOf(accessToken) {
    const parts = accessToken.split('.')
    if (parts.length !== 3) return undefined
    try {
        const p = JSON.parse(Buffer.from(parts[1], 'base64url').toString('utf8'))
        const who = p.name ?? p.preferred_username ?? p.sub
        return typeof who === 'string' && who ? who : undefined
    } catch {
        return undefined
    }
}

/**
 * The command that opens a URL: AGENTS_BROWSER (a JSON array, the URL appended) when set.
 * @param {string} url
 * @returns {{cmd: string, args: string[], verbatim: boolean}}
 */
function browserCommand(url) {
    const custom = process.env.AGENTS_BROWSER
    if (custom) {
        const argv = JSON.parse(custom)
        return { cmd: argv[0], args: [...argv.slice(1), url], verbatim: false }
    }
    // cmd parses the line itself: the quotes keep & in the query string out of its hands.
    if (process.platform === 'win32') return { cmd: 'cmd.exe', args: ['/d', '/s', '/c', `start "" "${url}"`], verbatim: true }
    if (process.platform === 'darwin') return { cmd: 'open', args: [url], verbatim: false }
    return { cmd: 'xdg-open', args: [url], verbatim: false }
}

/**
 * @param {string} url
 * @returns {Promise<string|undefined>} why the browser could not be started, or undefined
 */
function openBrowser(url) {
    let c
    try {
        c = browserCommand(url)
    } catch (e) {
        return Promise.resolve(`AGENTS_BROWSER is not a JSON array: ${/** @type {Error} */ (e).message}`)
    }
    return new Promise((resolve) => {
        let child
        try {
            child = spawn(c.cmd, c.args, { detached: true, stdio: 'ignore', windowsHide: true, windowsVerbatimArguments: c.verbatim })
        } catch (e) {
            resolve(/** @type {Error} */ (e).message)
            return
        }
        child.once('error', (e) => resolve(`${c.cmd}: ${e.message}`))
        child.once('spawn', () => {
            child.unref()
            resolve(undefined)
        })
    })
}

/**
 * @typedef {{authorizeUrl: string, done: Promise<{origin: string, who: string|undefined}>}} Flow
 */

/** @type {Map<string, Flow>} */
const flows = new Map()

/** @param {string} title @param {string} text */
const page = (title, text) => `<!doctype html><meta charset="utf-8"><title>${title}</title><p style="font:16px system-ui;margin:3em">${text}</p>`

/**
 * Start a sign-in: a loopback listener, the authorize URL, and a promise that settles when the
 * callback has been checked and the code exchanged and stored.
 * @param {string} url
 * @returns {Promise<Flow>}
 */
async function startFlow(url) {
    const origin = originOf(url)
    const meta = await discover(url)
    const client = await clientId(meta, origin)
    const verifier = b64url(crypto.randomBytes(32))
    const challenge = b64url(crypto.createHash('sha256').update(verifier).digest())
    const state = b64url(crypto.randomBytes(16))

    /** @type {(v: {code: string, redirectUri: string}) => void} */
    let gotCode = () => {}
    /** @type {(e: Error) => void} */
    let failed = () => {}
    const callback = new Promise((resolve, reject) => {
        gotCode = resolve
        failed = reject
    })

    const server = http.createServer((req, res) => {
        const u = new URL(req.url ?? '/', 'http://127.0.0.1')
        if (u.pathname !== '/callback') {
            res.writeHead(404).end()
            return
        }
        const send = (/** @type {number} */ status, /** @type {string} */ title, /** @type {string} */ text) => {
            res.writeHead(status, { 'Content-Type': 'text/html; charset=utf-8' })
            res.end(page(title, text))
        }
        if (u.searchParams.get('state') !== state) {
            send(400, 'Sign-in refused', 'The sign-in answer did not match the request this computer made, so it was refused. Run agent_signin again.')
            failed(new Refusal(`the sign-in callback's state did not match the request (a forged or stale answer); nothing was stored. Run ${signinCall(url)} again.`))
            return
        }
        const error = u.searchParams.get('error')
        if (error) {
            send(400, 'Sign-in failed', `The server answered: ${error.replace(/[<>&"]/g, '')}. You can close this tab.`)
            failed(new Refusal(`the server refused the sign-in: ${error}${u.searchParams.get('error_description') ? ` (${u.searchParams.get('error_description')})` : ''}`))
            return
        }
        const code = u.searchParams.get('code')
        if (!code) {
            send(400, 'Sign-in failed', 'The answer had no code. Run agent_signin again.')
            failed(new Refusal('the sign-in callback had no code'))
            return
        }
        send(200, 'Signed in', `Signed in to ${origin}. You can close this tab.`)
        gotCode({ code, redirectUri: `http://127.0.0.1:${/** @type {import('node:net').AddressInfo} */ (server.address()).port}/callback` })
    })
    await new Promise((resolve, reject) => {
        server.once('error', reject)
        server.listen(0, '127.0.0.1', () => resolve(undefined))
    })
    const port = /** @type {import('node:net').AddressInfo} */ (server.address()).port
    const redirectUri = `http://127.0.0.1:${port}/callback`
    const authorizeUrl = `${meta.authorization_endpoint}?${new URLSearchParams({
        response_type: 'code',
        client_id: client,
        redirect_uri: redirectUri,
        scope: SCOPE,
        state,
        code_challenge: challenge,
        code_challenge_method: 'S256',
        resource: meta.resource,
    })}`

    const timer = setTimeout(() => failed(new Refusal(`no sign-in arrived within ${SIGNIN_TIMEOUT_MS / 60000} minutes; nothing was stored. Run ${signinCall(url)} again.`)), SIGNIN_TIMEOUT_MS)
    timer.unref()

    const done = (async () => {
        try {
            const { code } = await callback
            const res = await postForm(meta.token_endpoint, {
                grant_type: 'authorization_code',
                code,
                redirect_uri: redirectUri,
                client_id: client,
                code_verifier: verifier,
                resource: meta.resource,
            })
            const j = await bodyJson(res)
            if (!res.ok || typeof j.access_token !== 'string') throw new Refusal(`exchanging the sign-in code failed: ${oauthReason(j, res.status)}`)
            if (typeof j.refresh_token !== 'string') throw new Refusal('the server issued no refresh token (offline_access not granted); nothing was stored')
            const refresh = j.refresh_token
            const scope = typeof j.scope === 'string' ? j.scope : SCOPE
            await withGrantsLock(async () => {
                const grants = readGrants()
                grants[origin] = { client_id: client, refresh_token: refresh, scope, obtained_at: new Date().toISOString() }
                writeGrants(grants)
            })
            cacheAccess(origin, j.access_token, j.expires_in)
            return { origin, who: subjectOf(j.access_token) }
        } finally {
            clearTimeout(timer)
            server.close()
            flows.delete(origin)
        }
    })()
    done.catch(() => {})
    return { authorizeUrl, done }
}

/**
 * agent_signin. Joins a sign-in already waiting for the same origin.
 * @param {string} url
 * @returns {Promise<{text: string, structured: Record<string, unknown>}>}
 */
export async function signIn(url) {
    const origin = originOf(url)
    let flow = flows.get(origin)
    if (!flow) {
        flow = await startFlow(url)
        flows.set(origin, flow)
        const notOpened = await openBrowser(flow.authorizeUrl)
        if (notOpened) {
            return {
                text: [
                    `could not open a browser (${notOpened}). Open this URL to sign in to ${origin}:`,
                    flow.authorizeUrl,
                    `It waits ${SIGNIN_TIMEOUT_MS / 60000} minutes; call ${signinCall(url)} again to wait for it to finish.`,
                ].join('\n'),
                structured: { signed_in: false, origin, authorize_url: flow.authorizeUrl, waiting: true },
            }
        }
    }
    let r
    try {
        r = await flow.done
    } catch (e) {
        if (e instanceof Refusal) throw new Refusal(`${e.message}\nsign-in URL was: ${flow.authorizeUrl}`)
        throw e
    }
    return {
        text: [
            `signed in to ${r.origin}${r.who ? ` as ${r.who}` : ''}`,
            `the grant is stored in ${grantsFile()} (never shown); agent_launch and agent_stop use it for ${r.origin} when no workstate.env_file is given`,
            `sign-in URL was: ${flow.authorizeUrl}`,
        ].join('\n'),
        structured: { signed_in: true, origin: r.origin, as: r.who ?? null, grants_file: grantsFile(), authorize_url: flow.authorizeUrl },
    }
}

/**
 * agent_signout: revoke the refresh token at the server, then forget it. The client_id stays.
 * @param {string} url
 * @returns {Promise<{text: string, structured: Record<string, unknown>}>}
 */
export async function signOut(url) {
    const origin = originOf(url)
    return withGrantsLock(async () => {
        const grants = readGrants()
        const g = grants[origin]
        if (!g?.refresh_token) {
            accessCache.delete(origin)
            return { text: `not signed in to ${origin}; nothing to revoke`, structured: { signed_out: false, origin, revoked: false } }
        }
        const meta = await discover(url)
        if (!meta.revocation_endpoint) throw new Refusal(`${origin} offers no revocation_endpoint; the grant is kept in ${grantsFile()}`)
        let res
        try {
            res = await postForm(meta.revocation_endpoint, { token: g.refresh_token, token_type_hint: 'refresh_token', client_id: g.client_id })
        } catch (e) {
            throw new Refusal(`revoking the grant failed: ${/** @type {Error} */ (e).message}. The grant is kept in ${grantsFile()}; retry agent_signout.`)
        }
        if (!res.ok) throw new Refusal(`revoking the grant failed: ${oauthReason(await bodyJson(res), res.status)}. The grant is kept in ${grantsFile()}; retry agent_signout.`)
        grants[origin] = { client_id: g.client_id }
        writeGrants(grants)
        accessCache.delete(origin)
        return {
            text: [
                `signed out of ${origin}: the grant is revoked at the server and deleted from ${grantsFile()}`,
                'every agent session minted with it is refused on its next request; agents launched with workstate.env_file are not affected',
                `sign in again with ${signinCall(url)}`,
            ].join('\n'),
            structured: { signed_out: true, origin, revoked: true },
        }
    })
}

/** @type {Map<string, {token: string, expiresAt: number}>} */
const accessCache = new Map()

/** @param {string} origin @param {string} token @param {unknown} expiresIn seconds */
function cacheAccess(origin, token, expiresIn) {
    const s = typeof expiresIn === 'number' && expiresIn > 0 ? expiresIn : 3600
    accessCache.set(origin, { token, expiresAt: Date.now() + s * 1000 - EXPIRY_SKEW_MS })
}

/** @param {string} url */
export function forgetAccessToken(url) {
    accessCache.delete(originOf(url))
}

/** @param {string} url */
function notSignedIn(url) {
    return new Refusal(`not signed in to ${originOf(url)}: run ${signinCall(url)} once to sign in in the browser. A headless launch instead needs workstate.env_file: the PATH of WorkState's .env (never its contents).`)
}

/**
 * Throws the agent_signin refusal unless a grant is stored for url. Reads the disk only.
 * @param {string} url
 */
export function requireGrant(url) {
    if (!readGrants()[originOf(url)]?.refresh_token) throw notSignedIn(url)
}

/**
 * An access token for url's server: cached until it expires, else refreshed with the stored grant,
 * whose rotated refresh token replaces the old one.
 * @param {string} url
 * @returns {Promise<string>}
 */
export async function accessToken(url) {
    const origin = originOf(url)
    const hit = accessCache.get(origin)
    if (hit && hit.expiresAt > Date.now()) return hit.token
    requireGrant(url)
    const meta = await discover(url)
    return withGrantsLock(async () => {
        const grants = readGrants()
        const g = grants[origin]
        if (!g?.refresh_token) throw notSignedIn(url)
        let res
        try {
            res = await postForm(meta.token_endpoint, { grant_type: 'refresh_token', refresh_token: g.refresh_token, client_id: g.client_id, resource: meta.resource })
        } catch (e) {
            throw new Refusal(`refreshing the WorkState sign-in failed: ${/** @type {Error} */ (e).message}. Nothing was launched.`)
        }
        const j = await bodyJson(res)
        if (!res.ok || typeof j.access_token !== 'string') {
            if (j.error === 'invalid_grant') {
                grants[origin] = { client_id: g.client_id }
                writeGrants(grants)
                throw new Refusal(`the stored sign-in for ${origin} was refused (invalid_grant: expired or revoked) and is forgotten: run ${signinCall(url)} to sign in again, or pass workstate.env_file.`)
            }
            throw new Refusal(`refreshing the WorkState sign-in failed: ${oauthReason(j, res.status)}. Nothing was launched.`)
        }
        if (typeof j.refresh_token === 'string' && j.refresh_token !== g.refresh_token) {
            grants[origin] = { ...g, refresh_token: j.refresh_token, scope: typeof j.scope === 'string' ? j.scope : g.scope }
            writeGrants(grants)
        }
        cacheAccess(origin, j.access_token, j.expires_in)
        return j.access_token
    })
}
