// program.mjs — the Tier-2 substrate: a LanguageService over the project's files.
//
// A LanguageService, not a bare Program, because findReferences / getImplementations /
// call-hierarchy are only exposed there — and because it versions files, which is exactly what
// lets the daemon apply edits by bumping a number instead of rebuilding.
// @ts-check

import fs from 'node:fs'
import path from 'node:path'
import { UserError, key } from './common.mjs'

/**
 * @typedef {object} Svc
 * @property {any} service
 * @property {() => any} getProgram
 * @property {(files: string[]) => number} refresh   re-stat and bump changed files; returns count
 */

/** @type {Map<string, Svc>} */
const perProject = new Map()

/**
 * @param {import('./discovery.mjs').Project} p
 * @param {{ services?: Map<string, Svc> }} [ctx]
 * @returns {any} the LanguageService
 */
export function getService(p, ctx) {
    const store = ctx?.services ?? perProject
    const existing = store.get(p.id)
    if (existing) {
        existing.refresh(p.fileNames)
        return existing.service
    }

    const ts = p.ts
    if (!p.fileNames.length) throw new UserError('no files in the program — check `tss discover`')

    /** @type {Map<string, {version: number, mtime: number, size: number, text?: string}>} */
    const files = new Map()
    const stat = (f) => {
        try {
            const s = fs.statSync(f)
            return { mtime: s.mtimeMs, size: s.size }
        } catch {
            return { mtime: 0, size: 0 }
        }
    }
    for (const f of p.fileNames) {
        const s = stat(f)
        files.set(f, { version: 1, mtime: s.mtime, size: s.size })
    }

    const host = {
        getScriptFileNames: () => [...files.keys()],
        getScriptVersion: (f) => String(files.get(f)?.version ?? 1),
        getScriptSnapshot: (f) => {
            const text = ts.sys.readFile(f)
            return text === undefined ? undefined : ts.ScriptSnapshot.fromString(text)
        },
        getCurrentDirectory: () => p.root,
        getCompilationSettings: () => p.options,
        getDefaultLibFileName: (o) => ts.getDefaultLibFilePath(o),
        fileExists: ts.sys.fileExists,
        readFile: ts.sys.readFile,
        readDirectory: ts.sys.readDirectory,
        directoryExists: ts.sys.directoryExists,
        getDirectories: ts.sys.getDirectories,
        realpath: ts.sys.realpath,
        useCaseSensitiveFileNames: () => ts.sys.useCaseSensitiveFileNames,
    }

    const service = ts.createLanguageService(host, ts.createDocumentRegistry())

    /** Bump the version of every file whose mtime/size moved, and add/drop files. */
    const refresh = (current) => {
        let changed = 0
        const seen = new Set()
        for (const f of current) {
            seen.add(f)
            const s = stat(f)
            const cur = files.get(f)
            if (!cur) {
                files.set(f, { version: 1, mtime: s.mtime, size: s.size })
                changed++
            } else if (cur.mtime !== s.mtime || cur.size !== s.size) {
                cur.version++
                cur.mtime = s.mtime
                cur.size = s.size
                changed++
            }
        }
        for (const f of [...files.keys()]) {
            if (!seen.has(f)) {
                files.delete(f)
                changed++
            }
        }
        return changed
    }

    const svc = { service, getProgram: () => service.getProgram(), refresh }
    store.set(p.id, svc)
    return service
}

/**
 * A second Program with the unused-checks turned on. Kept separate from the LanguageService
 * because flipping noUnusedLocals on the shared options would invalidate every cached tree for
 * every other command.
 */
export function unusedDiagnosticsProgram(p) {
    const ts = p.ts
    return ts.createProgram(p.fileNames, {
        ...p.options,
        noUnusedLocals: true,
        noUnusedParameters: true,
        noEmit: true,
    })
}

/**
 * Every declaration the user could mean by `<symbol>`, matched at dotted boundaries the same way
 * dotnet-source matches `Ns.Type.Member`: `Foo`, `Bar.Foo` and `a/b.Bar.Foo` all select `Foo`.
 * @param {Map<string, import('./decls.mjs').FileFacts>} facts
 * @param {string} symbol
 */
export function resolveSymbol(facts, symbol) {
    const parts = symbol.split('.').filter(Boolean)
    const name = parts[parts.length - 1]
    const qualifier = parts.slice(0, -1).join('.')

    /** @type {{file: string, decl: import('./decls.mjs').Decl}[]} */
    const hits = []
    for (const [, f] of facts) {
        for (const d of f.decls) {
            if (d.name !== name) continue
            if (qualifier) {
                const owner = d.container
                const relPath = f.file.split(path.sep).join('/')
                const q = qualifier.toLowerCase()
                if (!(owner?.toLowerCase() === q || relPath.toLowerCase().includes(q))) continue
            }
            hits.push({ file: f.file, decl: d })
        }
    }
    return hits
}
