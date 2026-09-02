// discovery.mjs — what the tool actually sees: the tsconfig, the file set, the workspace packages,
// and the entry points that seed reachability.
//
// The tsconfig is authoritative for the FILE SET, exactly as the .slnx is in dotnet-source: a naive
// `**/*.ts` glob would ingest node_modules, dist, and every worktree copy, and quietly corrupt
// every count. `discover` prints what was resolved so a wrong number is diagnosable in one command.
// @ts-check

import path from 'node:path'
import fs from 'node:fs'
import { UserError, key, globToRegExp, sha } from './common.mjs'
import { loadTypeScript } from './tsapi.mjs'

/** Files we never treat as source, whatever the tsconfig says. */
const ALWAYS_SKIP = /[/\\](node_modules|dist|build|out|coverage|\.turbo|\.next|\.output|dev-dist|\.git)[/\\]/i

/** Generated output that would otherwise double-count declarations. */
const GENERATED = /(\.d\.ts|\.gen\.ts|\.generated\.tsx?)$/i

const TEST_FILE = /(\.(test|spec)\.[cm]?[jt]sx?$|[/\\]__(tests?|mocks?)__[/\\])/i

/**
 * Config-ish modules that are executed by tooling, never imported by app code. Without this they
 * all show up as "dead files" — vite.config.ts is the single most common false positive there is.
 */
const CONFIG_FILE = /[/\\][^/\\]*\.config\.[cm]?[jt]sx?$/i

/**
 * @typedef {object} Project
 * @property {string} root            directory the tsconfig lives in
 * @property {string} configPath      the tsconfig used ('' when --root was given)
 * @property {any} options            parsed compilerOptions
 * @property {string[]} fileNames     the program's input files, absolute
 * @property {string[]} sourceFiles   fileNames minus declaration/generated files
 * @property {string} id              stable hash of (configPath|root) — keys the cache and daemon
 * @property {WorkspacePkg[]} packages
 * @property {any} ts                 the loaded compiler
 */

/**
 * @typedef {object} WorkspacePkg
 * @property {string} name
 * @property {string} dir
 * @property {string[]} entries  paths named by main/module/exports/bin
 */

/**
 * Resolve the project from flags, or by walking up from cwd.
 * @param {import('./args.mjs').Args} a
 * @returns {Project}
 */
export function resolveProject(a) {
    const cwd = process.cwd()
    const explicitRoot = a.str('root')
    const explicitConfig = a.str('project') ?? a.str('tsconfig')

    let configPath = ''
    let root = ''

    if (explicitConfig) {
        const p = path.resolve(explicitConfig)
        const asFile = fs.existsSync(p) && fs.statSync(p).isDirectory()
            ? path.join(p, 'tsconfig.json')
            : p
        if (!fs.existsSync(asFile)) throw new UserError(`no tsconfig at ${asFile}`)
        configPath = asFile
        root = path.dirname(asFile)
    } else if (explicitRoot) {
        root = path.resolve(explicitRoot)
        if (!fs.existsSync(root)) throw new UserError(`--root ${root} does not exist`)
        const found = findConfigUpward(root, root)
        if (found) configPath = found
    } else {
        const found = findConfigUpward(cwd)
        if (found) {
            configPath = found
            root = path.dirname(found)
        } else {
            root = cwd
        }
    }

    const ts = loadTypeScript(root, a.str('ts'))

    let options
    let fileNames
    if (configPath) {
        const read = ts.readConfigFile(configPath, ts.sys.readFile)
        if (read.error) {
            throw new UserError(`cannot read ${configPath}: ${ts.flattenDiagnosticMessageText(read.error.messageText, ' ')}`)
        }
        const parsed = ts.parseJsonConfigFileContent(read.config, ts.sys, path.dirname(configPath), undefined, configPath)
        // Errors here are usually a bad `extends` or an unknown option — worth surfacing, but not
        // fatal: a partially-parsed config still yields a usable file set.
        options = parsed.options
        fileNames = parsed.fileNames

        // TS project references: a monorepo splits its program across them, and a symbol declared in
        // one is used from another. Union them so find-usages isn't blind across the seam.
        const refs = parsed.projectReferences ?? []
        for (const r of refs) {
            try {
                const rp = ts.resolveProjectReferencePath(r)
                const rr = ts.readConfigFile(rp, ts.sys.readFile)
                if (rr.error) continue
                const rparsed = ts.parseJsonConfigFileContent(rr.config, ts.sys, path.dirname(rp), undefined, rp)
                fileNames = fileNames.concat(rparsed.fileNames)
            } catch { /* a broken reference must not take the whole command down */ }
        }
    } else {
        options = { allowJs: true, jsx: ts.JsxEmit.Preserve, target: ts.ScriptTarget.ESNext, module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler }
        fileNames = scanDir(root)
    }

    const includeGenerated = a.bool('include-generated')
    const seen = new Set()
    const files = []
    for (const f of fileNames) {
        const abs = path.resolve(f)
        if (ALWAYS_SKIP.test(abs)) continue
        const k = key(abs)
        if (seen.has(k)) continue
        seen.add(k)
        files.push(abs)
    }

    const sourceFiles = files.filter((f) => includeGenerated || !GENERATED.test(f))

    return {
        root,
        configPath,
        options,
        fileNames: files,
        sourceFiles,
        id: sha(key(configPath || root)),
        packages: findWorkspacePackages(root),
        ts,
    }
}

/** Cheap variant for the daemon probe: identity only, no config parsing, no compiler load. */
export function resolveIdOnly(a) {
    const explicitConfig = a.str('project') ?? a.str('tsconfig')
    if (explicitConfig) {
        const p = path.resolve(explicitConfig)
        const asFile = fs.existsSync(p) && fs.statSync(p).isDirectory() ? path.join(p, 'tsconfig.json') : p
        return sha(key(asFile))
    }
    const explicitRoot = a.str('root')
    if (explicitRoot) {
        const r = path.resolve(explicitRoot)
        const found = findConfigUpward(r, r)
        return sha(key(found || r))
    }
    const found = findConfigUpward(process.cwd())
    return sha(key(found || process.cwd()))
}

function findConfigUpward(from, stopAt = '') {
    let dir = path.resolve(from)
    for (;;) {
        const p = path.join(dir, 'tsconfig.json')
        if (fs.existsSync(p)) return p
        const parent = path.dirname(dir)
        if (parent === dir) return ''
        if (stopAt && key(dir) === key(stopAt)) return ''
        dir = parent
    }
}

const SOURCE_EXT = /\.[cm]?[jt]sx?$/i

function scanDir(root) {
    /** @type {string[]} */
    const found = []
    /** @param {string} dir */
    const walk = (dir) => {
        let entries
        try {
            entries = fs.readdirSync(dir, { withFileTypes: true })
        } catch {
            return
        }
        for (const e of entries) {
            const p = path.join(dir, e.name)
            if (e.isDirectory()) {
                if (ALWAYS_SKIP.test(p + path.sep)) continue
                walk(p)
            } else if (SOURCE_EXT.test(e.name)) {
                found.push(p)
            }
        }
    }
    walk(root)
    return found
}

/**
 * Workspace members, for grouping in `tree` and for the "a package's exports field IS its API"
 * caveat in `dead`. Reads pnpm-workspace.yaml and package.json `workspaces`.
 */
function findWorkspacePackages(root) {
    /** @type {WorkspacePkg[]} */
    const pkgs = []
    /** @type {string[]} */
    let patterns = []

    // Walk up: the workspace manifest usually sits above the tsconfig we resolved.
    let dir = root
    for (let i = 0; i < 6; i++) {
        const pnpm = path.join(dir, 'pnpm-workspace.yaml')
        if (fs.existsSync(pnpm)) {
            patterns = readPnpmPackages(pnpm).map((g) => path.resolve(dir, g))
            break
        }
        const pj = path.join(dir, 'package.json')
        if (fs.existsSync(pj)) {
            try {
                const j = JSON.parse(fs.readFileSync(pj, 'utf8'))
                const ws = Array.isArray(j.workspaces) ? j.workspaces : j.workspaces?.packages
                if (Array.isArray(ws) && ws.length) {
                    patterns = ws.map((g) => path.resolve(dir, g))
                    break
                }
            } catch { /* an unparseable package.json just means no workspace info */ }
        }
        const parent = path.dirname(dir)
        if (parent === dir) break
        dir = parent
    }

    if (!patterns.length) {
        const own = path.join(root, 'package.json')
        if (fs.existsSync(own)) pkgs.push(readPkg(root))
        return pkgs.filter(Boolean)
    }

    const base = dir
    const regexes = patterns.map((p) => globToRegExp(p))
    /** @param {string} d @param {number} depth */
    const walk = (d, depth) => {
        if (depth > 4) return
        let entries
        try {
            entries = fs.readdirSync(d, { withFileTypes: true })
        } catch {
            return
        }
        for (const e of entries) {
            if (!e.isDirectory()) continue
            if (e.name === 'node_modules' || e.name.startsWith('.')) continue
            const p = path.join(d, e.name)
            if (regexes.some((r) => r.test(p.split(path.sep).join('/'))) && fs.existsSync(path.join(p, 'package.json'))) {
                const pkg = readPkg(p)
                if (pkg) pkgs.push(pkg)
            }
            walk(p, depth + 1)
        }
    }
    walk(base, 0)
    return pkgs
}

/** Minimal YAML read: we only need the `packages:` list, so a real parser would be overkill. */
function readPnpmPackages(file) {
    /** @type {string[]} */
    const globs = []
    let inPackages = false
    for (const raw of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
        const line = raw.replace(/#.*$/, '')
        if (/^packages\s*:/.test(line)) {
            inPackages = true
            continue
        }
        if (inPackages) {
            const m = line.match(/^\s*-\s*["']?([^"'\s]+)["']?\s*$/)
            if (m) globs.push(m[1])
            else if (line.trim() && !/^\s/.test(line)) inPackages = false
        }
    }
    return globs
}

/** @returns {WorkspacePkg|null} */
function readPkg(dir) {
    try {
        const j = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'))
        /** @type {string[]} */
        const entries = []
        const push = (v) => {
            if (typeof v === 'string') entries.push(v)
            else if (v && typeof v === 'object') for (const k of Object.values(v)) push(k)
        }
        push(j.main); push(j.module); push(j.browser); push(j.exports); push(j.bin); push(j.types)
        return { name: j.name ?? path.basename(dir), dir, entries }
    } catch {
        return null
    }
}

/**
 * Seed set for reachability. Getting this wrong is the #1 source of false "dead file" reports, so
 * it is deliberately generous — a missed entry point is a wrong answer, an extra one is only a
 * missed opportunity.
 * @param {Project} p
 * @param {import('./args.mjs').Args} a
 * @returns {{files: string[], why: Map<string,string>}}
 */
export function entryPoints(p, a) {
    /** @type {Map<string,string>} */
    const why = new Map()
    const add = (f, reason) => {
        if (!f) return
        const abs = path.resolve(f)
        const k = key(abs)
        if (!why.has(k)) why.set(k, reason)
    }

    const known = new Map(p.sourceFiles.map((f) => [key(f), f]))

    // 1. HTML shells — the true entry of a Vite app. `<script type="module" src="/src/index.tsx">`
    for (const html of findHtml(p.root)) {
        const text = fs.readFileSync(html, 'utf8')
        for (const m of text.matchAll(/<script[^>]*\ssrc=["']([^"']+)["']/gi)) {
            const rel = m[1].replace(/^\//, '')
            add(path.resolve(p.root, rel), `html:${path.basename(html)}`)
        }
    }

    // 2. Package manifests: main/module/exports/bin are, by definition, entered from outside.
    for (const pkg of p.packages) {
        for (const e of pkg.entries) {
            const cand = path.resolve(pkg.dir, e)
            add(cand, `pkg:${pkg.name}`)
            // dist/foo.js in the manifest usually means src/foo.ts in the sources.
            for (const guess of sourceGuesses(cand, pkg.dir)) {
                if (known.has(key(guess))) add(guess, `pkg:${pkg.name}`)
            }
        }
    }

    // 3. Files nothing imports *by design*: tool configs, scripts, tests, service workers.
    //
    // The script rule is anchored at a package root on purpose. An unanchored `/tools/` match also
    // swallows `src/features/tools/**` — which silently marks a whole feature as an entry point and
    // hides every dead file inside it. A wrong entry point is worse than no entry point: it turns a
    // finding into silence, and silence is the one failure the user cannot see.
    const scriptRoots = [p.root, ...p.packages.map((x) => x.dir)]
    const isScriptDir = (f) => scriptRoots.some((base) => {
        const rel = path.relative(base, f)
        if (rel.startsWith('..') || path.isAbsolute(rel)) return false
        return /^(scripts|bin|tools)[/\\]/i.test(rel)
    })

    for (const f of p.sourceFiles) {
        if (CONFIG_FILE.test(f)) add(f, 'config')
        else if (TEST_FILE.test(f)) add(f, 'test')
        else if (isScriptDir(f)) add(f, 'script')
        else if (/[/\\](sw|service-worker|serviceWorker)\.[cm]?[jt]sx?$/i.test(f)) add(f, 'service-worker')
    }

    // 4. User-supplied, always wins.
    const extra = a.str('entry')
    if (extra) {
        for (const g of extra.split(',')) {
            const re = globToRegExp(path.resolve(p.root, g.trim()).split(path.sep).join('/'))
            for (const f of p.sourceFiles) {
                if (re.test(f.split(path.sep).join('/'))) add(f, 'flag')
            }
        }
    }

    // Keep only entries that are actually in the program; a manifest can name a built artifact.
    /** @type {string[]} */
    const files = []
    /** @type {Map<string,string>} */
    const kept = new Map()
    for (const [k, reason] of why) {
        const real = known.get(k)
        if (real) {
            files.push(real)
            kept.set(k, reason)
        }
    }
    return { files, why: kept }
}

/** dist/x.js -> src/x.ts|tsx, and the same name with a source extension. */
function sourceGuesses(cand, pkgDir) {
    /** @type {string[]} */
    const out = []
    const rel = path.relative(pkgDir, cand)
    const stripped = rel.replace(/^(dist|build|lib|out)[/\\]/i, '')
    const noExt = stripped.replace(/\.[cm]?[jt]sx?$/i, '')
    for (const base of [noExt, path.join('src', noExt)]) {
        for (const ext of ['.ts', '.tsx', '.mts', '.js', '.jsx']) {
            out.push(path.resolve(pkgDir, base + ext))
        }
        for (const ext of ['.ts', '.tsx', '.js']) {
            out.push(path.resolve(pkgDir, base, 'index' + ext))
        }
    }
    return out
}

function findHtml(root) {
    /** @type {string[]} */
    const found = []
    /** @param {string} dir @param {number} depth */
    const walk = (dir, depth) => {
        if (depth > 3) return
        let entries
        try {
            entries = fs.readdirSync(dir, { withFileTypes: true })
        } catch {
            return
        }
        for (const e of entries) {
            const p2 = path.join(dir, e.name)
            if (e.isDirectory()) {
                if (e.name === 'node_modules' || e.name.startsWith('.') || /^(dist|build|out|public|dev-dist)$/i.test(e.name)) continue
                walk(p2, depth + 1)
            } else if (/\.html?$/i.test(e.name)) {
                found.push(p2)
            }
        }
    }
    walk(root, 0)
    return found
}

export { TEST_FILE }
