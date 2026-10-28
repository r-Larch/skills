// agent_signin / agent_signout and agent_launch / agent_stop with the stored grant, against a fake
// WorkState that is its own OAuth server. The browser is test/fake-browser.mjs (AGENTS_BROWSER).
// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { startServer, makeRepo, tmp, CONTROLLER, CONTROLLER_TOKEN } from './helpers.mjs'
import { fakeOAuthWorkstate } from './fake-oauth.mjs'

function grantsHome() {
    const file = path.join(tmp('home'), '.workstate', 'grants.json')
    return { file, env: { WORKSTATE_GRANTS_FILE: file } }
}

/** @param {string} file @returns {Record<string, any>} */
const readGrants = (file) => JSON.parse(fs.readFileSync(file, 'utf8'))

/** @param {ReturnType<typeof makeRepo>} repo @param {string} name @param {Record<string, unknown>} workstate */
const launchArgs = (repo, name, workstate) => ({ name, brief: 'task.md', repo_root: repo.root, controller: CONTROLLER, workstate })

/** @param {Awaited<ReturnType<typeof fakeOAuthWorkstate>>} ws @param {string} grantType */
const tokenCalls = (ws, grantType) => ws.requests.filter((q) => q.path === '/connect/token' && q.body?.grant_type === grantType)

/** @param {Awaited<ReturnType<typeof fakeOAuthWorkstate>>} ws @param {string} token */
async function mcpAnswers(ws, token) {
    const res = await fetch(`${ws.url}/mcp`, { method: 'POST', headers: { Authorization: `Bearer ${token}` } })
    return res.status
}

test('agent_signin: discovery, DCR once per origin, PKCE S256, loopback callback; the grant is stored, no token returned', async () => {
    const ws = await fakeOAuthWorkstate()
    const home = grantsHome()
    const s = startServer(home.env)
    try {
        const r = await s.call('agent_signin', { url: ws.url })
        assert.equal(r.isError, false, r.content[0].text)
        assert.match(r.content[0].text, new RegExp(`^signed in to ${ws.url} as rene$`, 'm'))
        assert.match(r.content[0].text, /\/connect\/authorize\?/, 'the URL is returned for opening by hand')

        const authorize = ws.requests.find((q) => q.path === '/connect/authorize')
        assert.ok(authorize, 'the browser was sent to the authorize endpoint')
        const register = ws.requests.find((q) => q.path === '/connect/register')
        assert.deepEqual(register?.body.grant_types, ['authorization_code', 'refresh_token'])
        assert.equal(register?.body.token_endpoint_auth_method, 'none')
        const exchange = tokenCalls(ws, 'authorization_code')[0]
        assert.match(exchange.body.redirect_uri, /^http:\/\/127\.0\.0\.1:\d+\/callback$/)
        assert.equal(exchange.body.resource, `${ws.url}/api`)

        const grants = readGrants(home.file)
        assert.deepEqual(Object.keys(grants), [ws.url])
        const g = grants[ws.url]
        assert.deepEqual(Object.keys(g).sort(), ['client_id', 'obtained_at', 'refresh_token', 'scope'])
        assert.equal(g.client_id, 'client-1')
        assert.ok(ws.secrets.includes(g.refresh_token))
        if (process.platform !== 'win32') {
            assert.equal(fs.statSync(home.file).mode & 0o777, 0o600)
            assert.equal(fs.statSync(path.dirname(home.file)).mode & 0o777, 0o700)
        }
        assert.ok(!fs.readdirSync(path.dirname(home.file)).some((f) => f.endsWith('.tmp') || f.endsWith('.lock')), 'no temp or lock file left behind')

        const again = await s.call('agent_signin', { url: ws.url })
        assert.equal(again.isError, false, again.content[0].text)
        assert.equal(ws.count('/connect/register'), 1, 'the client_id is reused for the same origin')
    } finally {
        await s.close()
        await ws.close()
    }
})

test('agent_launch without env_file mints with the stored grant in a new process; PATCH and agent_stop use it too', async () => {
    const ws = await fakeOAuthWorkstate()
    const repo = makeRepo()
    const home = grantsHome()
    const first = startServer(home.env)
    const s = startServer(home.env)
    try {
        assert.equal((await first.call('agent_signin', { url: ws.url })).isError, false)
        await first.close()

        const r = await s.call('agent_launch', launchArgs(repo, 'oa-1', { url: ws.url }))
        assert.equal(r.isError, false, r.content[0].text)
        assert.equal(tokenCalls(ws, 'refresh_token').length, 1, 'a new process refreshes the stored grant')

        const mint = ws.requests.find((q) => q.method === 'POST' && q.path === '/api/agent-sessions')
        assert.match(mint?.auth ?? '', /^Bearer eyJ/, 'minted with the OAuth access token')
        assert.equal(mint?.body.role, 'worker')
        const [minted] = ws.minted()
        const mcp = JSON.parse(fs.readFileSync(path.join(repo.briefs, 'mcp-oa-1.json'), 'utf8'))
        assert.equal(mcp.mcpServers.workstate.headers.Authorization, `Bearer ${minted}`, 'the agent gets the minted wss_ token, unchanged')
        assert.equal(await mcpAnswers(ws, minted), 200, 'the worker can report into that server')
        assert.equal(ws.requests.find((q) => q.method === 'PATCH')?.auth, mint?.auth)

        const stop = await s.call('agent_stop', { name: 'oa-1' })
        assert.equal(stop.isError, false, stop.content[0].text)
        assert.equal(stop.structuredContent.revoked, true)
        assert.equal(ws.requests.find((q) => q.method === 'DELETE')?.auth, mint?.auth)
        assert.equal(tokenCalls(ws, 'refresh_token').length, 1, 'the access token is cached until it expires')
    } finally {
        await s.close()
        await ws.close()
    }
})

test('refresh rotation: every refresh stores the rotated token, and the next refresh redeems it', async () => {
    const ws = await fakeOAuthWorkstate({ expiresIn: 1 })
    const repo = makeRepo()
    const home = grantsHome()
    const s = startServer(home.env)
    try {
        assert.equal((await s.call('agent_signin', { url: ws.url })).isError, false)
        const signedIn = readGrants(home.file)[ws.url].refresh_token
        for (const name of ['rot-1', 'rot-2']) {
            const r = await s.call('agent_launch', launchArgs(repo, name, { url: ws.url }))
            assert.equal(r.isError, false, `${name}: ${r.content[0].text}`)
        }
        const refreshes = tokenCalls(ws, 'refresh_token')
        assert.equal(refreshes.length, 2)
        assert.equal(refreshes[0].body.refresh_token, signedIn)
        assert.notEqual(refreshes[1].body.refresh_token, signedIn, 'the second refresh redeems the rotated token')
        assert.equal(readGrants(home.file)[ws.url].refresh_token, ws.secrets.filter((x) => x.startsWith('wsrFAKErefresh')).at(-1))
    } finally {
        await s.close()
        await ws.close()
    }
})

test('no grant, or a refresh refused with invalid_grant, refuses the launch naming agent_signin; nothing spawns', async () => {
    const ws = await fakeOAuthWorkstate()
    const repo = makeRepo()
    const home = grantsHome()
    const s = startServer(home.env)
    const later = startServer(home.env)
    const signin = new RegExp(`agent_signin \\{"url": "${ws.url}"\\}`)
    try {
        for (const dryRun of [false, true]) {
            const r = await s.call('agent_launch', { ...launchArgs(repo, 'ng-1', { url: ws.url }), dry_run: dryRun })
            assert.equal(r.isError, true)
            assert.match(r.content[0].text, signin)
        }
        assert.equal(ws.requests.length, 0, 'no grant: refused before any request')

        assert.equal((await s.call('agent_signin', { url: ws.url })).isError, false)
        const dry = await s.call('agent_launch', { ...launchArgs(repo, 'ng-dry', { url: ws.url }), dry_run: true })
        assert.equal(dry.isError, false, dry.content[0].text)

        ws.expireRefreshTokens()
        const r = await later.call('agent_launch', launchArgs(repo, 'ng-2', { url: ws.url }))
        assert.equal(r.isError, true)
        assert.match(r.content[0].text, /invalid_grant/)
        assert.match(r.content[0].text, signin)
        assert.equal(readGrants(home.file)[ws.url].refresh_token, undefined, 'the dead grant is forgotten')
        assert.ok(!fs.existsSync(path.join(repo.briefs, 'mcp-ng-2.json')))
        assert.equal(later.claudeCalls().length + s.claudeCalls().length, 0)
        assert.equal(ws.minted().length, 0)
    } finally {
        await s.close()
        await later.close()
        await ws.close()
    }
})

test('agent_signout revokes at /connect/revoke, deletes the grant, and the sessions minted with it are refused', async () => {
    const ws = await fakeOAuthWorkstate()
    const repo = makeRepo()
    const home = grantsHome()
    const s = startServer(home.env)
    try {
        assert.equal((await s.call('agent_signin', { url: ws.url })).isError, false)
        const refresh = readGrants(home.file)[ws.url].refresh_token
        assert.equal((await s.call('agent_launch', launchArgs(repo, 'so-1', { url: ws.url }))).isError, false)
        const [minted] = ws.minted()
        assert.equal(await mcpAnswers(ws, minted), 200)

        const r = await s.call('agent_signout', { url: ws.url })
        assert.equal(r.isError, false, r.content[0].text)
        assert.match(r.content[0].text, /refused on its next request/)
        const revoke = ws.requests.find((q) => q.path === '/connect/revoke')
        assert.equal(revoke?.body.token, refresh)
        assert.equal(revoke?.body.token_type_hint, 'refresh_token')
        assert.deepEqual(readGrants(home.file)[ws.url], { client_id: 'client-1' })
        assert.equal(await mcpAnswers(ws, minted), 401, 'the session minted under the grant stops working')

        const twice = await s.call('agent_signout', { url: ws.url })
        assert.match(twice.content[0].text, /not signed in/)
        const after = await s.call('agent_launch', launchArgs(repo, 'so-2', { url: ws.url }))
        assert.equal(after.isError, true)
        assert.match(after.content[0].text, /agent_signin/)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('env_file wins over a stored grant, and env_file launches work unchanged after sign-out', async () => {
    const ws = await fakeOAuthWorkstate({ staticToken: CONTROLLER_TOKEN })
    const repo = makeRepo()
    const home = grantsHome()
    const s = startServer(home.env)
    try {
        assert.equal((await s.call('agent_signin', { url: ws.url })).isError, false)
        const r = await s.call('agent_launch', launchArgs(repo, 'ef-1', { url: ws.url, env_file: repo.envFile }))
        assert.equal(r.isError, false, r.content[0].text)
        const mint = ws.requests.find((q) => q.method === 'POST' && q.path === '/api/agent-sessions')
        assert.equal(mint?.auth, `Bearer ${CONTROLLER_TOKEN}`)
        assert.equal(tokenCalls(ws, 'refresh_token').length, 0)

        assert.equal((await s.call('agent_signout', { url: ws.url })).isError, false)
        const [first] = ws.minted()
        assert.equal(await mcpAnswers(ws, first), 200, 'signing out does not touch env_file sessions')
        const again = await s.call('agent_launch', launchArgs(repo, 'ef-2', { url: ws.url, env_file: repo.envFile }))
        assert.equal(again.isError, false, again.content[0].text)
        const stop = await s.call('agent_stop', { name: 'ef-2' })
        assert.equal(stop.structuredContent.revoked, true)
        assert.equal(ws.requests.filter((q) => q.method === 'DELETE').at(-1)?.auth, `Bearer ${CONTROLLER_TOKEN}`)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('a callback whose state does not match is refused, and nothing is exchanged or stored', async () => {
    const ws = await fakeOAuthWorkstate({ forgeState: true })
    const home = grantsHome()
    const s = startServer(home.env)
    try {
        const r = await s.call('agent_signin', { url: ws.url })
        assert.equal(r.isError, true)
        assert.match(r.content[0].text, /state did not match/)
        assert.equal(tokenCalls(ws, 'authorization_code').length, 0)
        assert.equal(readGrants(home.file)[ws.url].refresh_token, undefined)
    } finally {
        await s.close()
        await ws.close()
    }
})

test('when no browser can be started, agent_signin returns the URL to open by hand at once', async () => {
    const ws = await fakeOAuthWorkstate()
    const home = grantsHome()
    const s = startServer({ ...home.env, AGENTS_BROWSER: JSON.stringify([path.join(tmp('nobrowser'), 'no-such-browser')]) })
    try {
        const r = await s.call('agent_signin', { url: ws.url })
        assert.equal(r.isError, false, r.content[0].text)
        assert.equal(r.structuredContent.signed_in, false)
        assert.match(r.structuredContent.authorize_url, new RegExp(`^${ws.url}/connect/authorize\\?`))
        assert.match(r.content[0].text, /Open this URL to sign in/)
        for (const bad of [{ url: 'ftp://x' }, { url: ws.url, extra: 1 }]) {
            const refused = await s.call('agent_signin', bad)
            assert.equal(refused.isError, true)
        }
    } finally {
        await s.close()
        await ws.close()
    }
})
