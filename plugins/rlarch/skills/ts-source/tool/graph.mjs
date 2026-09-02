// graph.mjs — the module graph. THE structural thing C# does not have.
//
// In a .NET solution "is this used" can only be answered by asking the checker about every symbol.
// In an ES-module codebase the import graph answers a strictly stronger question — "is this file
// reachable from an entry point at all" — from syntax alone, with no program build. That is why
// `dead --files` and `dead --exports` are Tier 1 here and cost seconds, not a minute.
//
// The two things that make a naive version wrong, both handled below:
//   * barrels (`export * from './x'`) make every re-exported name look used by the barrel;
//   * `import * as ns` / `import('./x')` hide which names are touched, so the module must be
//     treated as fully used rather than fully dead.
// @ts-check

import path from 'node:path'
import { key } from './common.mjs'

/** Bundler-only imports: real edges for Vite, invisible to TS module resolution. */
const ASSET_SPEC = /\.(css|scss|sass|less|styl|svg|png|jpe?g|gif|webp|avif|woff2?|ttf|otf|mp[34]|webm|wasm|txt|md|html|glsl|frag|vert)(\?.*)?$/i

/**
 * @typedef {object} Graph
 * @property {Map<string, Set<string>>} imports    file -> files it pulls in
 * @property {Map<string, Set<string>>} importers  file -> files that pull it in
 * @property {Map<string, Set<string>>} consumed   file -> binding names other modules take from it
 * @property {Map<string, Set<string>>} starFrom   barrel file -> modules it re-exports wholesale
 * @property {Map<string, string[]>} unresolved    file -> specifiers that resolved to nothing local
 * @property {number} externalEdges                edges into node_modules (ignored, but counted)
 */

/**
 * @param {import('./discovery.mjs').Project} p
 * @param {Map<string, import('./decls.mjs').FileFacts>} facts
 * @returns {Graph}
 */
export function buildGraph(p, facts) {
    const ts = p.ts
    /** @type {Map<string, Set<string>>} */
    const imports = new Map()
    /** @type {Map<string, Set<string>>} */
    const importers = new Map()
    /** @type {Map<string, Set<string>>} */
    const consumed = new Map()
    /** @type {Map<string, Set<string>>} */
    const starFrom = new Map()
    /** @type {Map<string, string[]>} */
    const unresolved = new Map()
    let externalEdges = 0

    const known = new Map(p.sourceFiles.map((f) => [key(f), f]))
    const cache = ts.createModuleResolutionCache?.(p.root, (x) => x, p.options)

    const link = (from, to) => {
        if (!imports.has(from)) imports.set(from, new Set())
        imports.get(from).add(to)
        if (!importers.has(to)) importers.set(to, new Set())
        importers.get(to).add(from)
    }
    const take = (mod, name) => {
        if (!consumed.has(mod)) consumed.set(mod, new Set())
        consumed.get(mod).add(name)
    }

    for (const [k, f] of facts) {
        for (const e of f.edges) {
            const targets = e.kind === 'glob'
                ? resolveGlob(p, known, f.file, e.spec)
                : [resolveOne(ts, p, cache, f.file, e.spec, known)]

            let any = false
            for (const t of targets) {
                if (!t) continue
                any = true
                if (t === '__external__') {
                    externalEdges++
                    continue
                }
                link(k, t)
                if (e.kind === 'star-reexport') {
                    if (!starFrom.has(k)) starFrom.set(k, new Set())
                    starFrom.get(k).add(t)
                    // A star re-export also keeps the target FILE alive, independent of names.
                    take(t, '#star')
                } else {
                    for (const n of e.names) take(t, n)
                }
            }
            // A stylesheet or image import is a bundler edge, not a module edge. It never resolves
            // through TS module resolution and reporting it as broken would bury the real breakage.
            if (!any && e.spec.startsWith('.') && !ASSET_SPEC.test(e.spec)) {
                // A relative specifier that resolved to nothing is a real problem worth reporting.
                const list = unresolved.get(k) ?? []
                list.push(e.spec)
                unresolved.set(k, list)
            }
        }
    }

    // Charge names pulled from a barrel through to the module that actually declares them.
    // Fixed point rather than one pass: barrels nest (features/index -> chat/index -> chat/x).
    for (let pass = 0; pass < 12; pass++) {
        let changed = false
        for (const [barrel, sources] of starFrom) {
            const names = consumed.get(barrel)
            if (!names) continue
            for (const s of sources) {
                const dst = consumed.get(s) ?? new Set()
                const before = dst.size
                for (const n of names) dst.add(n)
                consumed.set(s, dst)
                if (dst.size !== before) changed = true
            }
        }
        if (!changed) break
    }

    return { imports, importers, consumed, starFrom, unresolved, externalEdges }
}

/**
 * @returns {string|null|'__external__'} the resolved local file key, '__external__' for a package,
 * or null when nothing resolved.
 */
function resolveOne(ts, p, cache, fromFile, spec, known) {
    try {
        const r = ts.resolveModuleName(spec, fromFile, p.options, ts.sys, cache)
        const rf = r.resolvedModule?.resolvedFileName
        if (rf) {
            if (rf.includes('node_modules')) return '__external__'
            const k = key(rf)
            return known.has(k) ? k : '__external__'
        }
    } catch { /* fall through to the manual guess */ }

    // Bare specifiers we can't resolve are packages; only relative ones are worth guessing at.
    if (!spec.startsWith('.')) return '__external__'

    // A .js specifier that means a .ts file (NodeNext style), or an extensionless directory index.
    const base = path.resolve(path.dirname(fromFile), spec)
    const stripped = base.replace(/\.[cm]?jsx?$/i, '')
    for (const cand of [
        base, stripped,
        ...['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs'].flatMap((x) => [stripped + x, base + x]),
        ...['index.ts', 'index.tsx', 'index.js', 'index.jsx'].map((x) => path.join(base, x)),
    ]) {
        const k = key(cand)
        if (known.has(k)) return k
    }
    return null
}

/** Vite's import.meta.glob — a real edge to every file the pattern covers. */
function resolveGlob(p, known, fromFile, pattern) {
    const abs = path.resolve(path.dirname(fromFile), pattern).split(path.sep).join('/')
    let re = ''
    for (let i = 0; i < abs.length; i++) {
        const c = abs[i]
        if (c === '*') {
            if (abs[i + 1] === '*') { re += '.*'; i++ } else re += '[^/]*'
        } else re += c.replace(/[.+^${}()|[\]\\?]/g, '\\$&')
    }
    const rx = new RegExp('^' + re + '$', 'i')
    /** @type {string[]} */
    const hits = []
    for (const [k] of known) if (rx.test(k)) hits.push(k)
    return hits
}

/**
 * Files reachable from the seeds, following import edges.
 * @param {Graph} g
 * @param {string[]} seeds absolute paths
 * @returns {Set<string>} keys
 */
export function reachable(g, seeds) {
    /** @type {Set<string>} */
    const seen = new Set()
    const queue = seeds.map((s) => key(s))
    for (const s of queue) seen.add(s)
    while (queue.length) {
        const cur = queue.pop()
        for (const next of g.imports.get(cur) ?? []) {
            if (seen.has(next)) continue
            seen.add(next)
            queue.push(next)
        }
    }
    return seen
}
