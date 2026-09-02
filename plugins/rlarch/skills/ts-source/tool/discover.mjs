// discover.mjs — "what does the tool actually see". The first command to run when a count looks
// wrong: it makes the file set, the entry points and the compiler in use visible in one shot.
// @ts-check

import path from 'node:path'
import { out, table, trailer, slash, key } from './common.mjs'
import { tsInfo } from './tsapi.mjs'
import { entryPoints } from './discovery.mjs'
import { buildIndex } from './cache.mjs'
import { buildGraph } from './graph.mjs'
import { getService } from './program.mjs'

export function discover(p, a, t0, ctx) {
    const ts = tsInfo()
    out(`project      ${p.configPath ? slash(path.relative(process.cwd(), p.configPath)) || slash(p.configPath) : '(no tsconfig — scanning --root)'}`)
    out(`root         ${slash(p.root)}`)
    out(`compiler     TypeScript ${ts.version}`)
    out(`             ${slash(ts.from)}`)
    out(`jsx          ${p.options.jsx !== undefined ? String(p.options.jsx) : '(unset)'}   importSource: ${p.options.jsxImportSource ?? '(default)'}`)
    out(`files        ${p.fileNames.length} in the program, ${p.sourceFiles.length} treated as source`)
    if (p.packages.length) {
        out(`packages     ${p.packages.length} workspace member(s): ${p.packages.map((x) => x.name).join(', ')}`)
    }

    const { facts, parsed, reused } = buildIndex(p, { memo: ctx?.memo })
    const g = buildGraph(p, facts)
    const { files: entries, why } = entryPoints(p, a)

    out('')
    out(`index        ${parsed} parsed, ${reused} from cache`)
    out(`graph        ${g.imports.size} files with outgoing edges, ${g.externalEdges} edges into node_modules`)
    out(`barrels      ${g.starFrom.size} file(s) using \`export * from\``)

    out('')
    out(`entry points (${entries.length}) — these seed reachability; a missed one shows up as a false "dead file":`)
    const byReason = new Map()
    for (const e of entries) {
        const r = why.get(key(e)) ?? '?'
        const list = byReason.get(r) ?? []
        list.push(slash(path.relative(p.root, e)))
        byReason.set(r, list)
    }
    table([...byReason.entries()].sort().map(([r, list]) => [
        '  ' + r,
        list.length > 6 ? `${list.slice(0, 6).join(', ')} … (+${list.length - 6})` : list.join(', '),
    ]))

    if (g.unresolved.size) {
        out('')
        out(`unresolved relative imports (${g.unresolved.size} file(s)) — these break the graph locally:`)
        let n = 0
        for (const [k, specs] of g.unresolved) {
            if (n++ >= 10) { out(`  … +${g.unresolved.size - 10} more`); break }
            out(`  ${slash(path.relative(p.root, facts.get(k)?.file ?? k))}: ${specs.join(', ')}`)
        }
    }

    if (a.bool('semantic')) {
        out('')
        const t = Date.now()
        const svc = getService(p, ctx)
        const prog = svc.getProgram()
        const srcs = prog.getSourceFiles().filter((f) => !f.isDeclarationFile && !f.fileName.includes('node_modules'))
        out(`program      ${srcs.length} source files, built in ${Date.now() - t} ms`)
        let unresolvedModules = 0
        for (const d of prog.getSemanticDiagnostics()) {
            if (d.code === 2307) unresolvedModules++   // "Cannot find module"
        }
        out(`references   ${unresolvedModules} unresolvable module(s) (code 2307).` +
            (unresolvedModules ? ' Tier-2 results near these are incomplete — run your install.' : ' Reference health is clean.'))
    } else {
        out('')
        out('// add --semantic to build the Program and check reference health (slower)')
    }

    trailer(`${p.sourceFiles.length} source files`, t0)
    return 0
}
