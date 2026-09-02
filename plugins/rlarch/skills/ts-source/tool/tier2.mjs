// tier2.mjs — semantic commands, built on the LanguageService.
//
// This is where JSX earns the tool its keep: `<Card variant="x"/>` is a reference to `Card`, and a
// grep for "Card" also matches CardHeader, a CSS class and a comment. findReferences does not.
// @ts-check

import path from 'node:path'
import { out, table, trailer, loc, slash, key, UserError } from './common.mjs'
import { buildIndex } from './cache.mjs'
import { getService, resolveSymbol } from './program.mjs'

/** Resolve the symbol the user named to one declaration position, or explain the ambiguity. */
function pick(p, a, facts, symbol, what) {
    const hits = resolveSymbol(facts, symbol)
    if (!hits.length) {
        throw new UserError(
            `no declaration named "${symbol}" in this project.\n` +
            `  ts-source only finds what YOUR sources declare. For a symbol from a package,\n` +
            '  read its types directly (node_modules/<pkg>) — this tool does not index them.\n' +
            `  Try: tss search ${symbol.split('.').pop()}`
        )
    }
    // Prefer an exported declaration, then the container-ish kinds; a same-named local is rarely it.
    const rank = (d) => (d.exported ? 0 : 4) + (d.container ? 1 : 0)
    hits.sort((x, y) => rank(x.decl) - rank(y.decl))

    const distinct = hits.filter((h) => h.decl.name === hits[0].decl.name)
    if (distinct.length > 1 && !a.str('at')) {
        const uniqueFiles = new Set(distinct.map((h) => key(h.file)))
        if (uniqueFiles.size > 1) {
            out(`// "${symbol}" is declared ${distinct.length} times — ${what} on the first; disambiguate with a qualifier:`)
            table(distinct.slice(0, 8).map((h) => [
                '  ' + loc(p.root, h.file, h.decl.line),
                h.decl.kind,
                h.decl.container ? `${h.decl.container}.${h.decl.name}` : h.decl.name,
            ]))
            out('')
        }
    }
    return hits[0]
}

export function findUsages(p, a, t0, ctx) {
    const symbol = a.first()
    if (!symbol) throw new UserError('find-usages needs a symbol: tss find-usages <Name|Type.Member>')
    const { facts: indexed } = buildIndex(p, { memo: ctx?.memo })
    const target = pick(p, a, indexed, symbol, 'find-usages')

    const svc = getService(p, ctx)
    const groups = svc.findReferences(target.file, target.decl.pos)
    if (!groups) {
        throw new UserError(`the compiler could not resolve "${symbol}" at ${loc(p.root, target.file, target.decl.line)} — run \`tss discover --semantic\``)
    }

    const prog = svc.getProgram()
    /** @type {{file: string, line: number, col: number, kind: string, text: string, hits: number}[]} */
    const refs = []
    /** @type {Map<string, number>} */
    const byLine = new Map()

    for (const gr of groups) {
        for (const e of gr.references) {
            const sf = prog.getSourceFile(e.fileName)
            if (!sf) continue
            const lc = sf.getLineAndCharacterOfPosition(e.textSpan.start)
            const line = lc.line + 1

            // An opening and a closing JSX tag on one line are two references with identical text.
            // Collapse them into one row with a count; two identical lines read as a rendering bug.
            const dedupe = `${key(e.fileName)}:${line}`
            const seen = byLine.get(dedupe)
            if (seen !== undefined) {
                refs[seen].hits++
                continue
            }

            const lineStart = sf.getPositionOfLineAndCharacter(lc.line, 0)
            const nl = sf.text.indexOf('\n', lineStart)
            const lineText = sf.text.slice(lineStart, nl < 0 ? sf.text.length : nl)

            // TS reports an import binding as a write access, which reads as "this line assigns to
            // it" — the opposite of what an import does. Name it for what it is.
            const facts = indexed.get(key(e.fileName))
            const isImportLine = facts?.edges.some((edge) => edge.line === line) ?? false

            byLine.set(dedupe, refs.length)
            refs.push({
                file: e.fileName,
                line,
                col: lc.character + 1,
                kind: e.isDefinition ? 'decl' : isImportLine ? 'import' : e.isWriteAccess ? 'write' : 'read',
                text: lineText.trim().replace(/\s+/g, ' ').slice(0, 120),
                hits: 1,
            })
        }
    }

    const order = { decl: 0, import: 2, write: 1, read: 3 }
    refs.sort((x, y) => order[x.kind] - order[y.kind] || x.file.localeCompare(y.file) || x.line - y.line)

    if (a.bool('json')) {
        out(JSON.stringify(refs.map((r) => ({ ...r, file: slash(path.relative(p.root, r.file)) })), null, 2))
        return 0
    }

    table(refs.map((r) => [
        loc(p.root, r.file, r.line),
        r.kind,
        r.hits > 1 ? `${r.text}   (x${r.hits})` : r.text,
    ]))

    const defs = refs.filter((r) => r.kind === 'decl').length
    const imports = refs.filter((r) => r.kind === 'import').length
    const uses = refs.reduce((n, r) => n + r.hits, 0) - defs - imports
    const files = new Set(refs.map((r) => key(r.file))).size
    if (!refs.length) out(`// no references to ${symbol}`)
    trailer(`${uses} usage(s) + ${imports} import(s) + ${defs} declaration(s) across ${files} file(s)`, t0)
    return 0
}

export function impls(p, a, t0, ctx) {
    const symbol = a.first()
    if (!symbol) throw new UserError('impls needs a type: tss impls <Interface|Class>')
    const { facts } = buildIndex(p, { memo: ctx?.memo })
    const target = pick(p, a, facts, symbol, 'impls')

    const svc = getService(p, ctx)
    const prog = svc.getProgram()
    const found = svc.getImplementationAtPosition(target.file, target.decl.pos) ?? []

    /** @type {string[][]} */
    const rows = []
    for (const impl of found) {
        const sf = prog.getSourceFile(impl.fileName)
        if (!sf) continue
        const lc = sf.getLineAndCharacterOfPosition(impl.textSpan.start)
        if (key(impl.fileName) === key(target.file) && lc.line + 1 === target.decl.line) continue
        rows.push([
            loc(p.root, impl.fileName, lc.line + 1),
            impl.kind ?? '',
            sf.text.substr(impl.textSpan.start, impl.textSpan.length),
        ])
    }

    // getImplementationAtPosition covers interfaces and abstract members; a plain `extends` chain
    // is a syntax question and the index already knows it. Union, so neither case is missed.
    if (a.bool('derived') || !rows.length) {
        const nameRe = new RegExp(`\\b(extends|implements)\\b[^{]*\\b${target.decl.name}\\b`)
        for (const [, f] of facts) {
            for (const d of f.decls) {
                if (!d.sig || d.container) continue
                if (!nameRe.test(d.sig)) continue
                const l = loc(p.root, f.file, d.line)
                if (rows.some((r) => r[0] === l)) continue
                rows.push([l, d.kind, `${d.name} ${d.sig}`])
            }
        }
    }

    rows.sort((x, y) => x[0].localeCompare(y[0]))
    table(rows)
    if (!rows.length) out(`// nothing implements or extends ${symbol}`)
    trailer(`${rows.length} implementation(s)/subtype(s) of ${target.decl.name}`, t0)
    return 0
}

export function calls(p, a, t0, ctx) {
    const symbol = a.first()
    if (!symbol) throw new UserError('calls needs a function: tss calls <fn> [--callers|--callees]')
    const { facts } = buildIndex(p, { memo: ctx?.memo })
    const target = pick(p, a, facts, symbol, 'calls')

    const svc = getService(p, ctx)
    const prog = svc.getProgram()
    const items = svc.prepareCallHierarchy(target.file, target.decl.pos)
    if (!items) throw new UserError(`${symbol} is not callable, or the compiler could not resolve it`)
    const item = Array.isArray(items) ? items[0] : items

    const wantCallees = a.bool('callees')
    const wantCallers = a.bool('callers') || !wantCallees

    if (wantCallers) {
        const incoming = svc.provideCallHierarchyIncomingCalls(item.file, item.selectionSpan.start) ?? []
        out(`callers of ${target.decl.name} (${incoming.length}):`)
        const rows = []
        for (const c of incoming) {
            const sf = prog.getSourceFile(c.from.file)
            const line = sf ? sf.getLineAndCharacterOfPosition(c.from.selectionSpan.start).line + 1 : 0
            rows.push([
                '  ' + loc(p.root, c.from.file, line),
                c.from.kind ?? '',
                c.from.name,
                `${c.fromSpans.length} call site(s)`,
            ])
        }
        table(rows)
        if (!rows.length) out('  // nobody calls it in this project')
    }

    if (wantCallees) {
        const outgoing = svc.provideCallHierarchyOutgoingCalls(item.file, item.selectionSpan.start) ?? []
        if (wantCallers) out('')
        out(`callees of ${target.decl.name} (${outgoing.length}):`)
        const rows = []
        for (const c of outgoing) {
            const sf = prog.getSourceFile(c.to.file)
            const line = sf ? sf.getLineAndCharacterOfPosition(c.to.selectionSpan.start).line + 1 : 0
            rows.push(['  ' + loc(p.root, c.to.file, line), c.to.kind ?? '', c.to.name, `${c.fromSpans.length} call site(s)`])
        }
        table(rows)
        if (!rows.length) out('  // it calls nothing declared in this project')
    }

    trailer(`call hierarchy for ${loc(p.root, target.file, target.decl.line)}`, t0)
    return 0
}
