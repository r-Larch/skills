// cache.mjs — the Tier-1 parse index: (path, mtime, size) -> FileFacts.
//
// Parsing 650 files costs ~1.2 s; stat-ing them costs ~25 ms. So we keep the facts on disk and
// re-parse only what changed. This is what makes a warm `search` feel instant without a daemon.
// @ts-check

import fs from 'node:fs'
import path from 'node:path'
import { cacheRoot, ensureDir, key } from './common.mjs'
import { parseFile } from './decls.mjs'

const FORMAT = 4   // bump when the FileFacts shape changes, so stale entries are discarded

/**
 * @typedef {object} IndexResult
 * @property {Map<string, import('./decls.mjs').FileFacts>} facts  keyed by `key(file)`
 * @property {number} parsed   files re-parsed this run
 * @property {number} reused   files served from the index
 */

/**
 * @param {import('./discovery.mjs').Project} p
 * @param {{ memo?: Map<string, any>, write?: boolean }} [opts]
 * @returns {IndexResult}
 */
export function buildIndex(p, opts = {}) {
    const write = opts.write ?? true
    const file = path.join(ensureDir(path.join(cacheRoot(), 'index')), `${p.id}.json`)

    /** @type {Record<string, {m: number, s: number, f: any}>} */
    let disk = {}
    if (!opts.memo) {
        try {
            const raw = JSON.parse(fs.readFileSync(file, 'utf8'))
            if (raw.format === FORMAT && raw.ts === p.ts.version) disk = raw.entries
        } catch { /* no index yet, or an unreadable one — reparse everything */ }
    }

    const memo = opts.memo
    /** @type {Map<string, import('./decls.mjs').FileFacts>} */
    const facts = new Map()
    /** @type {Record<string, {m: number, s: number, f: any}>} */
    const next = {}
    let parsed = 0
    let reused = 0
    let dirty = false

    for (const f of p.sourceFiles) {
        const k = key(f)
        let st
        try {
            st = fs.statSync(f)
        } catch {
            continue   // deleted between discovery and now
        }
        const m = st.mtimeMs
        const s = st.size

        const inMemo = memo?.get(k)
        if (inMemo && inMemo.m === m && inMemo.s === s) {
            facts.set(k, inMemo.f)
            next[k] = inMemo
            reused++
            continue
        }

        const hit = disk[k]
        if (hit && hit.m === m && hit.s === s) {
            facts.set(k, hit.f)
            next[k] = hit
            memo?.set(k, hit)
            reused++
            continue
        }

        let text
        try {
            text = fs.readFileSync(f, 'utf8')
        } catch {
            continue
        }
        const entry = { m, s, f: parseFile(p.ts, f, text) }
        facts.set(k, entry.f)
        next[k] = entry
        memo?.set(k, entry)
        parsed++
        dirty = true
    }

    // A removed file must drop out of the index too, or `dead` keeps reporting a ghost.
    if (!dirty && Object.keys(disk).length !== Object.keys(next).length) dirty = true

    if (write && dirty && !memo) {
        const tmp = `${file}.tmp-${process.pid}`
        try {
            fs.writeFileSync(tmp, JSON.stringify({ format: FORMAT, ts: p.ts.version, entries: next }))
            fs.renameSync(tmp, file)   // atomic: a torn index would poison every later run
        } catch {
            try { fs.unlinkSync(tmp) } catch { /* best effort */ }
        }
    }

    return { facts, parsed, reused }
}

/** Drop the on-disk index for this project. */
export function clearIndex(p) {
    try {
        fs.unlinkSync(path.join(cacheRoot(), 'index', `${p.id}.json`))
        return true
    } catch {
        return false
    }
}
