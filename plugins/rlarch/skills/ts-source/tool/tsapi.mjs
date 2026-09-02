// tsapi.mjs — locate and load the TypeScript compiler.
//
// We deliberately use the compiler the TARGET PROJECT resolves, not a version we pin. This is the
// mirror image of dotnet-source's "pin Roslyn" note, and it lands on the opposite answer for a good
// reason: an older parser turns newer syntax into error nodes and SILENTLY DROPS declarations, and
// in the npm world it is the project — not this tool — that decides how new the syntax is. Using
// the project's own compiler makes that self-pinning. We fall back to a copy resolvable from the
// tool itself, and finally to whatever is on NODE_PATH.
// @ts-check

import { createRequire } from 'node:module'
import path from 'node:path'
import fs from 'node:fs'
import { spawnSync } from 'node:child_process'
import { UserError, cacheRoot, ensureDir } from './common.mjs'

/** @type {any} */
let cached = null
/** @type {string} */
let cachedFrom = ''

/**
 * @param {string} startDir directory to resolve `typescript` from (the project)
 * @param {string} [override] explicit path to a typescript module or its lib/typescript.js
 */
export function loadTypeScript(startDir, override) {
    if (cached) return cached

    /** @type {string[]} */
    const tried = []

    const attempt = (spec, fromFile) => {
        try {
            const req = createRequire(fromFile)
            const resolved = req.resolve(spec)
            const mod = req(resolved)
            if (mod && typeof mod.createSourceFile === 'function') {
                cached = mod
                cachedFrom = resolved
                return mod
            }
            tried.push(`${resolved} (not a TypeScript compiler)`)
        } catch (e) {
            tried.push(`${spec} from ${path.dirname(fromFile)} — ${/** @type {Error} */ (e).message.split('\n')[0]}`)
        }
        return null
    }

    if (override) {
        const abs = path.resolve(override)
        const candidate = fs.existsSync(abs) && fs.statSync(abs).isDirectory()
            ? path.join(abs, 'lib', 'typescript.js')
            : abs
        if (fs.existsSync(candidate)) {
            const mod = createRequire(import.meta.url)(candidate)
            if (mod && typeof mod.createSourceFile === 'function') {
                cached = mod
                cachedFrom = candidate
                return mod
            }
        }
        throw new UserError(`--ts "${override}" is not a TypeScript compiler`)
    }

    // A file path, not a directory: createRequire resolves relative to the *containing* dir, and a
    // bare directory would make `./node_modules` lookups start one level too high.
    const probe = path.join(path.resolve(startDir), '__ts_source_probe__.cjs')
    const fromProject = attempt('typescript', probe)
    if (fromProject) return fromProject

    const fromTool = attempt('typescript', import.meta.url)
    if (fromTool) return fromTool

    // Last resort: a pinned compiler in our own cache. This is the ONLY thing the tool ever
    // installs, it happens once per machine, and it exists so a plain-JS repo (or one that hasn't
    // been installed yet) still gets Tier 1 — which needs a parser, not the project's exact
    // semantics. When the project does have TypeScript we never get here.
    const cached2 = attempt('typescript', path.join(vendorDir(), 'probe.cjs'))
    if (cached2) return cached2

    if (installVendored()) {
        const after = attempt('typescript', path.join(vendorDir(), 'probe.cjs'))
        if (after) return after
    }

    throw new UserError(
        'could not load the TypeScript compiler.\n' +
        `  Tried:\n    ${tried.join('\n    ')}\n` +
        '  Fix: run your package manager install in the project (so `typescript` is in node_modules),\n' +
        '  or pass --ts <path-to-typescript>.'
    )
}

const PINNED = 'typescript@5'

function vendorDir() {
    return ensureDir(path.join(cacheRoot(), 'vendor'))
}

/** @returns {boolean} whether a compiler is now present in the cache */
function installVendored() {
    const dir = vendorDir()
    if (fs.existsSync(path.join(dir, 'node_modules', 'typescript', 'package.json'))) return true

    // stderr, never stdout: stdout is the greppable result stream.
    process.stderr.write(`ts-source: no TypeScript found in the project — installing ${PINNED} into ${dir} (once)…\n`)
    try {
        fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify({ name: 'ts-source-vendor', private: true }))
        const r = spawnSync('npm', ['install', '--no-audit', '--no-fund', '--loglevel=error', PINNED], {
            cwd: dir,
            stdio: ['ignore', 'ignore', 'inherit'],
            shell: process.platform === 'win32',
            timeout: 180_000,
        })
        return r.status === 0
    } catch {
        return false
    }
}

/** Where the loaded compiler came from, and its version — for `discover`. */
export function tsInfo() {
    if (!cached) return { version: '(not loaded)', from: '' }
    return { version: cached.version, from: cachedFrom }
}
