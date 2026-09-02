// tier1.mjs — syntax-only commands: search, outline, tree, metrics, graph.
// No program, no checker, no build. Works on code that doesn't compile.
// @ts-check

import path from 'node:path'
import { out, table, trailer, loc, matcher, key, slash, UserError } from './common.mjs'
import { kindFilter } from './decls.mjs'
import { buildIndex } from './cache.mjs'
import { buildGraph } from './graph.mjs'

/** Shared preamble: resolve, index, and report how much was cached. */
function load(p, ctx) {
    return buildIndex(p, { memo: ctx?.memo })
}

/** All declarations, flattened, with their owning file. */
function* allDecls(facts) {
    for (const [, f] of facts) {
        for (const d of f.decls) yield { d, f }
    }
}

export function search(p, a, t0, ctx) {
    const pattern = a.first()
    if (!pattern) throw new UserError('search needs a pattern: tss search <pattern>')
    const { facts, parsed, reused } = load(p, ctx)

    const match = matcher(pattern, { regex: a.bool('regex') })
    const wantKind = kindFilter(a.list('kind'))
    const onlyExported = a.bool('exported')
    const limit = a.int('top', 200)

    const hits = []
    for (const { d, f } of allDecls(facts)) {
        if (!wantKind(d.kind)) continue
        if (onlyExported && !d.exported) continue
        if (!match(d.name)) continue
        hits.push({ d, f })
    }

    // Exact name first, then exported, then shortest name — the thing you searched for is almost
    // never the 40-character identifier that merely contains it.
    const needle = pattern.toLowerCase()
    hits.sort((x, y) => {
        const ex = (x.d.name.toLowerCase() === needle ? 0 : 1) - (y.d.name.toLowerCase() === needle ? 0 : 1)
        if (ex) return ex
        const exp = (y.d.exported ? 1 : 0) - (x.d.exported ? 1 : 0)
        if (exp) return exp
        return x.d.name.length - y.d.name.length || x.d.name.localeCompare(y.d.name)
    })

    if (a.bool('json')) {
        out(JSON.stringify(hits.slice(0, limit).map(({ d, f }) => ({ ...d, file: slash(path.relative(p.root, f.file)) })), null, 2))
        return 0
    }

    const rows = hits.slice(0, limit).map(({ d, f }) => [
        loc(p.root, f.file, d.line),
        d.exported ? (d.isDefault ? 'export default' : 'export') : '',
        d.kind,
        d.container ? `${d.container}.${d.name}` : d.name,
        d.sig,
    ])
    table(rows)
    if (!rows.length) out(`// no declaration matches "${pattern}"`)
    trailer(
        `${hits.length} match${hits.length === 1 ? '' : 'es'}${hits.length > limit ? ` (showing ${limit}, raise with --top)` : ''}` +
        ` in ${facts.size} files — ${parsed} parsed, ${reused} cached`,
        t0
    )
    return 0
}

export function outline(p, a, t0, ctx) {
    const { facts, parsed, reused } = load(p, ctx)
    const fileFlag = a.str('file')

    if (fileFlag) {
        const target = path.resolve(fileFlag)
        const f = facts.get(key(target))
        if (!f) throw new UserError(`${slash(path.relative(p.root, target))} is not in this project (see: tss discover)`)
        out(`${slash(path.relative(p.root, f.file))}   ${f.decls.length} declarations, ${f.loc} lines`)
        out('')
        const rows = f.decls
            .slice()
            .sort((x, y) => x.line - y.line)
            .map((d) => [
                String(d.line),
                d.exported ? (d.isDefault ? 'export default' : 'export') : '',
                d.kind,
                d.container ? `${d.container}.${d.name}` : d.name,
                d.sig,
            ])
        table(rows)
        trailer(`${f.decls.length} declarations — ${parsed} parsed, ${reused} cached`, t0)
        return 0
    }

    const name = a.first()
    if (!name) throw new UserError('outline needs a type or a --file: tss outline <Name> | tss outline --file <path>')
    const match = matcher(name, { regex: a.bool('regex') })

    // Container-ish declarations first; a `const` named the same is not what you asked to outline.
    const owners = []
    for (const { d, f } of allDecls(facts)) {
        if (d.container) continue
        if (!match(d.name)) continue
        owners.push({ d, f })
    }
    if (!owners.length) throw new UserError(`no declaration named "${name}" (try: tss search ${name})`)

    const rank = (k) => (k === 'class' || k === 'interface' ? 0 : k === 'type' || k === 'enum' ? 1 : k === 'component' ? 2 : 3)
    owners.sort((x, y) => rank(x.d.kind) - rank(y.d.kind) || (x.d.name === name ? -1 : 1))

    let shown = 0
    for (const { d, f } of owners.slice(0, a.int('top', 6))) {
        if (shown++) out('')
        // A parameter list / type annotation abuts the name; a heritage clause or a type body must
        // not (`class HttpErrorextends Error`).
        const gap = d.sig && !/^[([<:]/.test(d.sig) ? ' ' : ''
        out(`${d.exported ? (d.isDefault ? 'export default ' : 'export ') : ''}${d.kind} ${d.name}${gap}${d.sig}`)
        out(`  ${loc(p.root, f.file, d.line)}  (${d.endLine - d.line + 1} lines)`)
        if (d.doc) out(`  ${d.doc}`)

        const members = f.decls.filter((m) => m.container === d.name)
        if (members.length) {
            out('')
            const nonPublic = members.filter((m) => /^(private|protected) /.test(m.kind)).length
            table(members.map((m) => ['  ' + String(m.line), m.kind, m.name, m.sig]))
            out('')
            out(`  // ${members.length} members, ${nonPublic} of them non-public`)
        }
    }
    if (owners.length > a.int('top', 6)) out(`\n// ${owners.length - a.int('top', 6)} further declarations match — narrow the name or raise --top`)
    trailer(`${owners.length} matching declaration(s) — ${parsed} parsed, ${reused} cached`, t0)
    return 0
}

export function tree(p, a, t0, ctx) {
    const { facts } = load(p, ctx)
    const filter = a.first()
    const match = matcher(filter, { regex: a.bool('regex') })

    /** @type {Map<string, {files: number, decls: number, exports: number, loc: number}>} */
    const dirs = new Map()
    for (const [, f] of facts) {
        const rel = slash(path.relative(p.root, f.file))
        if (filter && !match(rel)) continue
        const dir = path.posix.dirname(rel)
        const cur = dirs.get(dir) ?? { files: 0, decls: 0, exports: 0, loc: 0 }
        cur.files++
        cur.decls += f.decls.length
        cur.exports += f.exportedNames.length
        cur.loc += f.loc
        dirs.set(dir, cur)
    }

    const rows = [...dirs.entries()]
        .sort((x, y) => x[0].localeCompare(y[0]))
        .map(([dir, s]) => [dir, `${s.files} files`, `${s.decls} decls`, `${s.exports} exported`, `${s.loc} loc`])
    table(rows)
    const totals = [...dirs.values()].reduce((acc, s) => ({
        files: acc.files + s.files, decls: acc.decls + s.decls, exports: acc.exports + s.exports, loc: acc.loc + s.loc,
    }), { files: 0, decls: 0, exports: 0, loc: 0 })
    trailer(`${dirs.size} directories, ${totals.files} files, ${totals.decls} declarations, ${totals.loc} lines`, t0)
    return 0
}

export function metrics(p, a, t0, ctx) {
    const { facts } = load(p, ctx)
    const sort = a.str('sort') ?? 'loc'
    const top = a.int('top', 30)
    const scope = a.str('of') ?? (sort === 'members' || sort === 'params' ? 'type' : 'file')

    if (scope === 'type') {
        const types = []
        for (const [, f] of facts) {
            for (const d of f.decls) {
                if (d.container) continue
                const members = f.decls.filter((m) => m.container === d.name)
                if (!members.length && d.kind !== 'component') continue
                types.push({
                    name: d.name,
                    kind: d.kind,
                    file: f.file,
                    line: d.line,
                    loc: d.endLine - d.line + 1,
                    members: members.length,
                    methods: members.filter((m) => m.kind.endsWith('method')).length,
                    params: Math.max(d.params, ...members.map((m) => m.params), 0),
                })
            }
        }
        const keyOf = (x) => x[sort] ?? x.loc
        types.sort((x, y) => keyOf(y) - keyOf(x))
        table(types.slice(0, top).map((x) => [
            loc(p.root, x.file, x.line),
            x.kind,
            x.name,
            `${x.loc} loc`,
            `${x.members} members`,
            `${x.methods} methods`,
        ]))
        trailer(`${types.length} types ranked by ${sort} (--of file to rank files instead)`, t0)
        return 0
    }

    const files = []
    for (const [k, f] of facts) {
        files.push({
            file: f.file,
            loc: f.loc,
            decls: f.decls.length,
            exports: f.exportedNames.length,
            imports: f.edges.length,
            components: f.decls.filter((d) => d.kind === 'component').length,
            k,
        })
    }
    const keyOf = (x) => x[sort] ?? x.loc
    files.sort((x, y) => keyOf(y) - keyOf(x))
    table(files.slice(0, top).map((x) => [
        slash(path.relative(p.root, x.file)),
        `${x.loc} loc`,
        `${x.decls} decls`,
        `${x.exports} exported`,
        `${x.imports} imports`,
        x.components ? `${x.components} components` : '',
    ]))
    trailer(`${files.length} files ranked by ${sort} — sort: loc|decls|exports|imports|components`, t0)
    return 0
}

/** Import edges for one file, in either direction. The "who pulls this in" question. */
export function graph(p, a, t0, ctx) {
    const target = a.first()
    if (!target) throw new UserError('graph needs a file: tss graph <path> [--importers|--imports]')
    const { facts } = load(p, ctx)
    const g = buildGraph(p, facts)

    const abs = path.resolve(target)
    let k = key(abs)
    if (!facts.has(k)) {
        // Accept a bare name too: `tss graph http.ts`.
        const cands = [...facts.keys()].filter((x) => x.endsWith('/' + target.split(path.sep).join('/').toLowerCase()))
        if (cands.length === 1) k = cands[0]
        else if (cands.length > 1) {
            out('// ambiguous, matches:')
            for (const c of cands) out('  ' + slash(path.relative(p.root, facts.get(c).file)))
            return 1
        } else throw new UserError(`${target} is not a file in this project (see: tss discover)`)
    }

    const both = !a.bool('importers') && !a.bool('imports')
    const self = facts.get(k)
    out(`${slash(path.relative(p.root, self.file))}   ${self.loc} lines, ${self.exportedNames.length} exports`)

    if (both || a.bool('importers')) {
        const who = [...(g.importers.get(k) ?? [])]
        out('')
        out(`  imported by (${who.length}):`)
        if (!who.length) out('    — nobody. Either an entry point, or dead (tss dead --files).')
        const names = g.consumed.get(k) ?? new Set()
        for (const w of who.sort()) out('    ' + slash(path.relative(p.root, facts.get(w)?.file ?? w)))
        if (names.size) out(`  names taken from it: ${[...names].sort().join(', ')}`)
    }
    if (both || a.bool('imports')) {
        const what = [...(g.imports.get(k) ?? [])]
        out('')
        out(`  imports (${what.length} local):`)
        for (const w of what.sort()) out('    ' + slash(path.relative(p.root, facts.get(w)?.file ?? w)))
    }
    trailer(`module graph: ${g.imports.size} files with edges, ${g.externalEdges} edges into node_modules`, t0)
    return 0
}
