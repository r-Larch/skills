// dead.mjs — what is never used. Three independent levels, each answering a different question:
//
//   --files    nothing reachable from an entry point imports this module        (graph, exact)
//   --exports  this module exports a name no other module ever takes            (graph, exact)
//   --locals   declared inside a file and never read there                      (checker)
//
// The wording matters, exactly as it does in dotnet-source: this answers "nothing in this project
// references it", which is not the same as "it is dead". A route loaded by string, a component
// mounted by a plugin, a `use:` directive — all are used without a reference. Verify before you
// delete; the levels are ordered so the safest signal comes first.
// @ts-check

import path from 'node:path'
import { out, table, trailer, slash, key, loc, UserError } from './common.mjs'
import { buildIndex } from './cache.mjs'
import { buildGraph, reachable } from './graph.mjs'
import { entryPoints, TEST_FILE } from './discovery.mjs'
import { unusedDiagnosticsProgram } from './program.mjs'

/** Diagnostics that mean "declared and never read". */
const UNUSED_CODES = new Set([
    6133, // 'X' is declared but its value is never read
    6138, // Property 'X' is declared but its value is never read
    6192, // All imports in import declaration are unused
    6196, // 'X' is declared but never used
    6198, // All destructured elements are unused
    6199, // All variables are unused
    6205, // All type parameters are unused
])

/**
 * A Solid/JSX directive (`use:autoresize`) is a real use of a function that no reference records.
 * Same class of problem as DI and reflection in .NET: skip rather than report a wrong answer.
 */
function directiveNames(facts) {
    /** @type {Set<string>} */
    const names = new Set()
    for (const [, f] of facts) {
        // Cheap and reliable: the attribute syntax is unambiguous.
        for (const m of (f.directives ?? [])) names.add(m)
    }
    return names
}

export function dead(p, a, t0, ctx) {
    const wantFiles = a.bool('files')
    const wantExports = a.bool('exports')
    const wantLocals = a.bool('locals')
    const none = !wantFiles && !wantExports && !wantLocals

    const { facts, parsed, reused } = buildIndex(p, { memo: ctx?.memo })
    const g = buildGraph(p, facts)
    const { files: entries } = entryPoints(p, a)
    const live = reachable(g, entries)
    const includeTests = a.bool('include-tests')

    let total = 0

    // Reachability with no seed marks the ENTIRE project dead — a confidently wrong answer, and
    // the worst possible failure mode for a command whose output people act on by deleting files.
    // Refuse instead, and say how to fix it.
    if (!entries.length) {
        throw new UserError(
            'no entry points found, so reachability cannot be computed — every file would be\n' +
            '  reported as dead, which is certainly wrong.\n' +
            '  ts-source seeds reachability from index.html <script> tags, package.json\n' +
            '  main/module/exports/bin, *.config.* files, scripts/ and tests.\n' +
            '  Fix: run from the app directory (so its tsconfig and index.html are found),\n' +
            '  or name them yourself:  tss dead --entry "src/index.ts,src/worker.ts"\n' +
            '  Run `tss discover` to see what was resolved.'
        )
    }

    // ---- level 1: files ---------------------------------------------------------------------
    if (none || wantFiles) {
        const orphans = []
        for (const [k, f] of facts) {
            if (live.has(k)) continue
            if (!includeTests && TEST_FILE.test(f.file)) continue
            orphans.push(f)
        }
        orphans.sort((x, y) => y.loc - x.loc)

        out(`DEAD FILES (${orphans.length}) — no entry point reaches them:`)
        if (!orphans.length) out('  // none; every file is reachable')

        // A plausible result is a small minority. A large one almost always means a missing entry
        // point, not a codebase that is one-third dead — say so rather than let it be believed.
        const share = facts.size ? orphans.length / facts.size : 0
        if (share > 0.35) {
            out('')
            out(`  // WARNING: ${Math.round(share * 100)}% of files are unreachable. That is far more likely to mean a`)
            out('  // missing entry point than genuinely dead code. Check `tss discover`, and add the')
            out('  // real entries with --entry before acting on this list.')
        }
        table(orphans.map((f) => [
            '  ' + slash(path.relative(p.root, f.file)),
            `${f.loc} loc`,
            `${f.decls.filter((d) => d.exported).length} exports`,
            f.decls.filter((d) => d.kind === 'component').map((d) => d.name).slice(0, 3).join(', '),
        ]))
        total += orphans.length
        out('')
    }

    // ---- level 2: exports -------------------------------------------------------------------
    if (none || wantExports) {
        const includePublic = a.bool('include-public')
        const pkgDirs = p.packages.map((x) => key(x.dir))
        const directives = directiveNames(facts)

        /** @type {{file: string, decl: any, why: string}[]} */
        const dead2 = []
        for (const [k, f] of facts) {
            if (!live.has(k)) continue                 // already reported as a dead FILE
            if (!includeTests && TEST_FILE.test(f.file)) continue

            const taken = g.consumed.get(k)
            // `import * as ns from './x'` or a dynamic import: we cannot see which names are used,
            // so every export must be assumed live. Under-reporting is the correct failure here.
            if (taken?.has('*')) continue

            // A workspace package's own entry file IS its public API — an export nothing local
            // takes is the point, not a defect. Same trade as dotnet-source's --include-public.
            const inPackageApi = !includePublic && pkgDirs.some((d) => key(f.file).startsWith(d + '/src/index'))
            if (inPackageApi) continue

            for (const d of f.decls) {
                if (!d.exported || d.container) continue
                const name = d.isDefault ? 'default' : d.name
                if (taken?.has(name)) continue
                if (directives.has(d.name)) continue
                dead2.push({ file: f.file, decl: d, why: (d.localRefs ?? 0) > 0 ? 'over-exported' : 'unreferenced' })
            }
        }
        dead2.sort((x, y) => x.file.localeCompare(y.file) || x.decl.line - y.decl.line)

        // Two very different actions, so two lists. Lumping them together is what makes most
        // dead-code reports unusable: "nobody imports this" covers both a function to delete and a
        // helper that is simply exported for no reason, and only one of them is worth your risk.
        const gone = dead2.filter((x) => x.why === 'unreferenced')
        const overExported = dead2.filter((x) => x.why === 'over-exported')

        out(`UNUSED EXPORTS — nothing imports the name (${dead2.length} total)`)
        out('')
        out(`  [1] DELETABLE (${gone.length}) — not imported anywhere AND not used in its own file:`)
        if (!gone.length) out('      // none')
        table(gone.map((x) => [
            '      ' + loc(p.root, x.file, x.decl.line),
            x.decl.kind,
            x.decl.name,
            x.decl.doc ? x.decl.doc.slice(0, 55) : '',
        ]))

        out('')
        out(`  [2] OVER-EXPORTED (${overExported.length}) — used only inside its own file; drop the \`export\`, don't delete:`)
        if (!overExported.length) out('      // none')
        const cap = a.int('top', 60)
        table(overExported.slice(0, cap).map((x) => [
            '      ' + loc(p.root, x.file, x.decl.line),
            x.decl.kind,
            x.decl.name,
            `${x.decl.localRefs} local use(s)`,
        ]))
        if (overExported.length > cap) out(`      // +${overExported.length - cap} more — raise --top`)
        total += dead2.length
        out('')
    }

    // ---- level 3: locals --------------------------------------------------------------------
    if (wantLocals) {
        const ts = p.ts
        const prog = unusedDiagnosticsProgram(p)
        /** @type {string[][]} */
        const rows = []
        for (const sf of prog.getSourceFiles()) {
            if (sf.isDeclarationFile || sf.fileName.includes('node_modules')) continue
            if (!facts.has(key(sf.fileName))) continue
            if (!includeTests && TEST_FILE.test(sf.fileName)) continue
            for (const d of prog.getSemanticDiagnostics(sf)) {
                if (!UNUSED_CODES.has(d.code)) continue
                const lc = sf.getLineAndCharacterOfPosition(d.start ?? 0)
                rows.push([
                    '  ' + loc(p.root, sf.fileName, lc.line + 1),
                    String(d.code),
                    ts.flattenDiagnosticMessageText(d.messageText, ' '),
                ])
            }
        }
        out(`UNUSED LOCALS & IMPORTS (${rows.length}) — declared inside a file, never read there:`)
        if (!rows.length) out('  // none')
        table(rows.slice(0, a.int('top', 200)))
        if (rows.length > a.int('top', 200)) out(`  // +${rows.length - a.int('top', 200)} more — raise --top`)
        total += rows.length
        out('')
    }

    const bits = [`${facts.size} files`, `${entries.length} entry points`, `${live.size} reachable`, `${parsed} parsed`, `${reused} cached`]
    if (!wantLocals) bits.push('add --locals for unused imports/variables (slower: builds a Program)')
    trailer(`${total} finding(s) — ${bits.join(', ')}`, t0)
    return 0
}
