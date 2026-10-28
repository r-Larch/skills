// fake-oauth.mjs — a fake WorkState that is also its own OAuth server, as WorkState 0.9.0 is:
// discovery, DCR, an authorize endpoint that approves at once, token with refresh rotation, revoke,
// and /api/agent-sessions that checks the Bearer. Every secret it issues is random per run and
// listed in `secrets`, so a leak check needs no fixture.
// @ts-check

import http from 'node:http'
import crypto from 'node:crypto'

/** @param {number} n */
const rand = (n) => crypto.randomBytes(n).toString('hex')

/**
 * @typedef {{method: string, path: string, auth: string|undefined, body: any}} Recorded
 * @typedef {{forgeState?: boolean, expiresIn?: number, staticToken?: string, subject?: string}} Options
 * @param {Options} [opts]
 */
export async function fakeOAuthWorkstate(opts = {}) {
    /** @type {Recorded[]} */
    const requests = []
    /** @type {string[]} every token, code and verifier this server issued or saw */
    const secrets = []
    const clients = new Set()
    /** @type {Map<string, {client: string, redirectUri: string, challenge: string, resource: string}>} */
    const codes = new Map()
    /** @type {Map<string, string>} live refresh token -> authorization id */
    const refreshTokens = new Map()
    /** @type {Map<string, string>} live access token -> authorization id */
    const accessTokens = new Map()
    /** @type {Map<string, {id: string, authz: string|null, live: boolean}>} minted wss_ token -> session */
    const sessions = new Map()
    const revokedAuthz = new Set()
    let nextSession = 1

    /** @param {string} authz */
    function issue(authz) {
        const payload = Buffer.from(JSON.stringify({ sub: opts.subject ?? 'rene', authz })).toString('base64url')
        const access = `eyJhbGciOiJub25lIn0.${payload}.wsaFAKEaccess${rand(12)}`
        const refresh = `wsrFAKErefresh${rand(16)}`
        accessTokens.set(access, authz)
        refreshTokens.set(refresh, authz)
        secrets.push(access, refresh)
        return { access_token: access, token_type: 'Bearer', expires_in: opts.expiresIn ?? 3600, refresh_token: refresh, scope: 'workstate.sessions offline_access' }
    }

    /** @param {string} authz */
    function revokeAuthorization(authz) {
        revokedAuthz.add(authz)
        for (const [t, a] of refreshTokens) if (a === authz) refreshTokens.delete(t)
        for (const [t, a] of accessTokens) if (a === authz) accessTokens.delete(t)
        for (const s of sessions.values()) if (s.authz === authz) s.live = false
    }

    const server = http.createServer((req, res) => {
        let raw = ''
        req.on('data', (d) => (raw += d))
        req.on('end', () => {
            const u = new URL(req.url ?? '/', origin)
            const form = (req.headers['content-type'] ?? '').startsWith('application/x-www-form-urlencoded')
            let body = null
            if (form) body = Object.fromEntries(new URLSearchParams(raw))
            else try { body = raw ? JSON.parse(raw) : null } catch { body = raw }
            requests.push({ method: req.method ?? '', path: u.pathname, auth: req.headers.authorization, body })
            const json = (/** @type {number} */ status, /** @type {unknown} */ v) => {
                res.writeHead(status, { 'Content-Type': 'application/json' })
                res.end(v === undefined ? '' : JSON.stringify(v))
            }
            const bearer = (req.headers.authorization ?? '').replace(/^Bearer /, '')
            const route = `${req.method} ${u.pathname}`

            if (route === 'GET /.well-known/oauth-protected-resource/api') return json(200, { resource: `${origin}/api`, authorization_servers: [origin] })
            if (route === 'GET /.well-known/oauth-authorization-server') {
                return json(200, {
                    issuer: `${origin}/`,
                    authorization_endpoint: `${origin}/connect/authorize`,
                    token_endpoint: `${origin}/connect/token`,
                    registration_endpoint: `${origin}/connect/register`,
                    revocation_endpoint: `${origin}/connect/revoke`,
                    code_challenge_methods_supported: ['S256'],
                })
            }
            if (route === 'POST /connect/register') {
                if (body?.token_endpoint_auth_method !== 'none') return json(400, { error: 'invalid_client_metadata' })
                const id = `client-${clients.size + 1}`
                clients.add(id)
                return json(201, { client_id: id, client_id_issued_at: 0, redirect_uris: body.redirect_uris, token_endpoint_auth_method: 'none' })
            }
            if (route === 'GET /connect/authorize') {
                const q = Object.fromEntries(u.searchParams)
                const redirect = new URL(q.redirect_uri ?? 'x:')
                const bad =
                    q.response_type !== 'code' ? 'response_type'
                        : !clients.has(q.client_id) ? 'client_id'
                            : q.code_challenge_method !== 'S256' || !q.code_challenge ? 'pkce'
                                : u.searchParams.getAll('resource').length !== 1 || q.resource !== `${origin}/api` ? 'invalid_target'
                                    : q.scope !== 'workstate.sessions offline_access' ? 'scope'
                                        : redirect.protocol !== 'http:' || redirect.hostname !== '127.0.0.1' || redirect.pathname !== '/callback' ? 'redirect_uri'
                                            : ''
                if (bad) return json(400, { error: bad })
                const code = `codeFAKE${rand(12)}`
                secrets.push(code)
                codes.set(code, { client: q.client_id, redirectUri: q.redirect_uri, challenge: q.code_challenge, resource: q.resource })
                redirect.searchParams.set('code', code)
                redirect.searchParams.set('state', opts.forgeState ? 'forged-state' : q.state)
                res.writeHead(302, { Location: redirect.toString() })
                return res.end()
            }
            if (route === 'POST /connect/token') {
                if (body?.grant_type === 'authorization_code') {
                    const c = codes.get(body.code)
                    codes.delete(body.code)
                    if (body.code_verifier) secrets.push(body.code_verifier)
                    if (!c || c.client !== body.client_id || c.redirectUri !== body.redirect_uri || c.resource !== body.resource) return json(400, { error: 'invalid_grant' })
                    const challenge = crypto.createHash('sha256').update(String(body.code_verifier)).digest('base64url')
                    if (challenge !== c.challenge) return json(400, { error: 'invalid_grant', error_description: 'PKCE' })
                    return json(200, issue(`authz-${rand(4)}`))
                }
                if (body?.grant_type === 'refresh_token') {
                    const authz = refreshTokens.get(body.refresh_token)
                    if (!authz) return json(400, { error: 'invalid_grant', error_description: 'The token is no longer valid.' })
                    refreshTokens.delete(body.refresh_token)
                    return json(200, issue(authz))
                }
                return json(400, { error: 'unsupported_grant_type' })
            }
            if (route === 'POST /connect/revoke') {
                const authz = refreshTokens.get(body?.token) ?? accessTokens.get(body?.token)
                if (authz) revokeAuthorization(authz)
                return json(200, {})
            }
            if (u.pathname.startsWith('/api/agent-sessions')) {
                const viaOAuth = accessTokens.get(bearer)
                if (!viaOAuth && !(opts.staticToken && bearer === opts.staticToken)) return json(401, { error: 'unauthorized' })
                if (route === 'POST /api/agent-sessions') {
                    const id = `ws-${nextSession++}`
                    const token = `wss_FAKEminted${rand(12)}`
                    secrets.push(token)
                    sessions.set(token, { id, authz: viaOAuth ?? null, live: true })
                    return json(201, { sessionId: id, name: body?.name, role: body?.role, token, expiresAt: '2026-10-29T00:00:00Z' })
                }
                const id = decodeURIComponent(u.pathname.split('/')[3] ?? '')
                const s = [...sessions.values()].find((x) => x.id === id)
                if (!s) return json(404, { error: 'not found' })
                if (req.method === 'PATCH') return json(200, { ok: true })
                if (req.method === 'DELETE') {
                    s.live = false
                    res.writeHead(204)
                    return res.end()
                }
            }
            if (route === 'POST /mcp') {
                const s = sessions.get(bearer)
                return s?.live ? json(200, { ok: true }) : json(401, { error: 'unauthorized' })
            }
            json(404, { error: 'not found' })
        })
    })
    await new Promise((r) => server.listen(0, '127.0.0.1', () => r(undefined)))
    const addr = /** @type {import('node:net').AddressInfo} */ (server.address())
    const origin = `http://127.0.0.1:${addr.port}`
    return {
        url: origin,
        requests,
        secrets,
        /** the minted wss_ tokens, in mint order */
        minted: () => [...sessions.keys()],
        /** the server forgets every refresh token, as when they all expired */
        expireRefreshTokens: () => refreshTokens.clear(),
        revokedAuthorizations: () => revokedAuthz.size,
        /** @param {string} path */
        count: (path) => requests.filter((q) => q.path === path).length,
        close: () => new Promise((r) => {
            server.closeAllConnections()
            server.close(() => r(undefined))
        }),
    }
}
