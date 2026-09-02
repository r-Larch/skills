// server.mjs — the optional warm daemon.
//
// Tier 1 has a disk index and needs no daemon. Tier 2 does: a LanguageService cannot be serialised,
// so every stateless `find-usages` pays the full program build. Holding one in a process turns that
// into a millisecond query.
//
// Identity is the PROJECT ID (the resolved tsconfig path), so two agents in the same tree reach the
// same daemon without coordinating, and two agents in different trees never collide. Requests are
// served ONE AT A TIME on purpose: handlers capture output by swapping a module-level sink, so
// concurrent handling would interleave two callers' results.
// @ts-check

import net from 'node:net'
import fs from 'node:fs'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { out, note, cacheRoot, ensureDir, captureOutput, UserError } from './common.mjs'
import { resolveProject, resolveIdOnly } from './discovery.mjs'

const IDLE_MS = 30 * 60 * 1000

function pipePath(id) {
    return process.platform === 'win32'
        ? `\\\\.\\pipe\\ts-source-${id}`
        : path.join(ensureDir(path.join(cacheRoot(), 'sock')), `${id}.sock`)
}

/** A presence marker, so a client can skip the connect attempt entirely when no daemon is up. */
function markerPath(id) {
    return path.join(ensureDir(path.join(cacheRoot(), 'daemon')), `${id}.json`)
}

function readMarker(id) {
    try {
        return JSON.parse(fs.readFileSync(markerPath(id), 'utf8'))
    } catch {
        return null
    }
}

/**
 * Ask a running daemon. Returns null — silently — when there isn't one; that is the normal case,
 * not an error.
 * @param {string} id
 * @param {{cmd: string, argv: string[]}} request
 * @returns {Promise<{output: string, exit: number}|null>}
 */
export function tryAsk(id, request) {
    const marker = readMarker(id)
    if (!marker) return Promise.resolve(null)

    return new Promise((resolve) => {
        let settled = false
        const done = (v) => {
            if (settled) return
            settled = true
            resolve(v)
        }

        const sock = net.connect(pipePath(id))
        // Generous: the daemon serves one request at a time, so a queued caller must not give up
        // and start a 20-second stateless build just because a peer got there first.
        sock.setTimeout(180_000)
        let buf = ''

        sock.on('connect', () => {
            sock.write(JSON.stringify({ ...request, cwd: process.cwd() }) + '\n')
        })
        sock.on('data', (d) => {
            buf += d.toString('utf8')
            const nl = buf.indexOf('\n')
            if (nl < 0) return
            try {
                done(JSON.parse(buf.slice(0, nl)))
            } catch {
                done(null)
            }
            sock.end()
        })
        sock.on('error', () => done(null))
        sock.on('timeout', () => { sock.destroy(); done(null) })
        sock.on('close', () => done(null))
    })
}

export async function serve(a, t0) {
    if (a.bool('stop')) return stop(a)

    const id = resolveIdOnly(a)

    // Already up? Say so and return the same pid, so N parallel agents produce ONE daemon.
    const existing = await tryAsk(id, { cmd: 'ping', argv: [] })
    if (existing) {
        out(existing.output.trim())
        return 0
    }

    if (!a.bool('foreground')) {
        // Re-exec ourselves detached, then wait for the marker. `serve` RETURNS once warm — the
        // caller must never have to background it, and an agent that does `&` gets a herd.
        const cli = fileURLToPath(new URL('./cli.mjs', import.meta.url))
        const args = [cli, 'serve', '--foreground', ...rebuildArgs(a)]
        const child = spawn(process.execPath, args, {
            detached: true,
            stdio: 'ignore',
            cwd: process.cwd(),
        })
        child.unref()

        const deadline = Date.now() + 120_000
        while (Date.now() < deadline) {
            const resp = await tryAsk(id, { cmd: 'ping', argv: [] })
            if (resp) {
                out(resp.output.trim())
                return 0
            }
            await sleep(150)
        }
        note('ts-source: the daemon did not become ready within 120 s')
        return 1
    }

    return foreground(a, id, t0)
}

function rebuildArgs(a) {
    /** @type {string[]} */
    const args = []
    for (const [k, v] of Object.entries(a.flags)) {
        if (k === 'foreground' || k === 'stop') continue
        if (v === true) args.push(`--${k}`)
        else args.push(`--${k}`, String(v))
    }
    return args
}

async function foreground(a, id, t0) {
    const { run } = await import('./main.mjs')
    const p = resolveProject(a)

    /** @type {Map<string, any>} */
    const memo = new Map()
    /** @type {Map<string, any>} */
    const services = new Map()

    // Warm both tiers before announcing readiness, so the first real query is fast.
    const { buildIndex } = await import('./cache.mjs')
    buildIndex(p, { memo })
    const { getService } = await import('./program.mjs')
    getService(p, { services }).getProgram()

    let idleTimer = null
    const resetIdle = () => {
        if (idleTimer) clearTimeout(idleTimer)
        idleTimer = setTimeout(() => {
            cleanup()
            process.exit(0)
        }, IDLE_MS)
        idleTimer.unref?.()
    }

    const sockPath = pipePath(id)
    if (process.platform !== 'win32') {
        try { fs.unlinkSync(sockPath) } catch { /* no stale socket */ }
    }

    /** @type {Promise<void>} */
    let chain = Promise.resolve()

    const server = net.createServer((sock) => {
        let buf = ''
        sock.on('data', (d) => {
            buf += d.toString('utf8')
            const nl = buf.indexOf('\n')
            if (nl < 0) return
            const line = buf.slice(0, nl)
            buf = buf.slice(nl + 1)

            let request
            try {
                request = JSON.parse(line)
            } catch {
                sock.end(JSON.stringify({ output: '', exit: 2 }) + '\n')
                return
            }

            // Serialize: the output sink is process-global.
            chain = chain.then(async () => {
                resetIdle()
                const restore = captureOutput()
                let exit = 0
                try {
                    if (request.cmd === 'ping') {
                        out(`daemon running (pid ${process.pid}) — ${p.sourceFiles.length} files, project ${p.configPath || p.root}`)
                    } else {
                        // Re-resolve the project each request so added/removed files are picked up;
                        // the memo and the service cache make it cheap.
                        const fresh = resolveProject(new (await import('./args.mjs')).Args(request.argv))
                        fresh.ts = p.ts
                        exit = await run([request.cmd, ...request.argv], {
                            memo,
                            services,
                            project: fresh,
                            inDaemon: true,
                        })
                    }
                } catch (e) {
                    out(`error: ${e instanceof UserError ? e.message : String(e?.stack ?? e)}`)
                    exit = 2
                }
                const output = restore()
                try {
                    sock.end(JSON.stringify({ output, exit }) + '\n')
                } catch { /* the caller gave up; nothing to do */ }
            })
        })
        sock.on('error', () => { /* a client that vanished must not kill the daemon */ })
    })

    const logFile = path.join(ensureDir(path.join(cacheRoot(), 'daemon')), `${id}.log`)

    await new Promise((resolve, reject) => {
        server.once('error', reject)
        server.listen(sockPath, () => resolve(undefined))
    })

    fs.writeFileSync(markerPath(id), JSON.stringify({
        pid: process.pid,
        project: p.configPath || p.root,
        started: new Date().toISOString(),
        log: logFile,
    }))

    const cleanup = () => {
        try { fs.unlinkSync(markerPath(id)) } catch { /* already gone */ }
        if (process.platform !== 'win32') {
            try { fs.unlinkSync(sockPath) } catch { /* already gone */ }
        }
    }
    process.on('exit', cleanup)
    process.on('SIGINT', () => { cleanup(); process.exit(0) })
    process.on('SIGTERM', () => { cleanup(); process.exit(0) })

    resetIdle()
    note(`ts-source: daemon started (pid ${process.pid}) — warm in ${Date.now() - t0} ms, ${p.sourceFiles.length} files`)
    return new Promise(() => { /* run until idle timeout or signal */ })
}

function stop(a) {
    const id = resolveIdOnly(a)
    const marker = readMarker(id)
    if (!marker) {
        out('// no daemon is serving this project')
        return 0
    }
    try {
        process.kill(marker.pid, 'SIGTERM')
        out(`daemon stopped (pid ${marker.pid})`)
    } catch {
        out(`// daemon (pid ${marker.pid}) was already gone`)
    }
    try { fs.unlinkSync(markerPath(id)) } catch { /* already gone */ }
    return 0
}

export async function status(a, t0) {
    const id = resolveIdOnly(a)
    const resp = await tryAsk(id, { cmd: 'ping', argv: [] })
    if (resp) {
        out(resp.output.trim())
        const marker = readMarker(id)
        if (marker) out(`log: ${marker.log}`)
        return 0
    }
    const marker = readMarker(id)
    if (marker) {
        out(`// a marker exists (pid ${marker.pid}) but the daemon does not answer — it died. Clearing.`)
        try { fs.unlinkSync(markerPath(id)) } catch { /* already gone */ }
        return 1
    }
    out('// no daemon is serving this project (Tier-2 commands will run stateless)')
    void t0
    return 0
}

function sleep(ms) {
    return new Promise((r) => setTimeout(r, ms))
}
