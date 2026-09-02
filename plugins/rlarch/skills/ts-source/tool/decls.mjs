// decls.mjs — Tier 1. One parse per file yields BOTH the declaration records and the module edges,
// because every command needs one or the other and re-parsing to get the second is pure waste.
//
// No type checker is involved. Signatures are the SOURCE TEXT as written (`Accessor<T>` stays
// `Accessor<T>`, a `const` keeps its inferred type unstated) — right for navigation, wrong for
// exact type identity. That is the same trade dotnet-source makes for its Tier 1.
// @ts-check

/**
 * @typedef {object} Decl
 * @property {string} name
 * @property {string} kind
 * @property {boolean} exported
 * @property {boolean} isDefault
 * @property {number} line
 * @property {number} endLine
 * @property {number} pos       character offset of the identifier — what findReferences needs
 * @property {string} sig
 * @property {string} container ''  for top level, else the owning class/interface name
 * @property {string} doc       first sentence of the leading /** … *\/ block, or ''
 * @property {number} params
 * @property {number} [localRefs]  uses of this name elsewhere in the SAME file
 */

/**
 * @typedef {object} Edge
 * @property {string} spec       module specifier as written
 * @property {string[]} names    imported/re-exported binding names; '*' means "everything"
 * @property {number} line
 * @property {'import'|'reexport'|'star-reexport'|'dynamic'|'require'|'glob'} kind
 */

/**
 * @typedef {object} FileFacts
 * @property {string} file
 * @property {Decl[]} decls
 * @property {Edge[]} edges
 * @property {string[]} exportedNames  every name this module exposes (incl. 'default')
 * @property {string[]} directives     JSX `use:x` directive names referenced in this file
 * @property {number} loc
 * @property {boolean} hasJsx
 */

const COMPONENT_NAME = /^[A-Z][A-Za-z0-9_]*$/
const HOOK_NAME = /^use[A-Z]/
const PRIMITIVE_NAME = /^create[A-Z]/

/**
 * Parse one file into facts. `ts` is the project's compiler.
 * @param {any} ts
 * @param {string} file
 * @param {string} text
 * @returns {FileFacts}
 */
export function parseFile(ts, file, text) {
    const scriptKind = /\.tsx$/i.test(file) ? ts.ScriptKind.TSX
        : /\.jsx$/i.test(file) ? ts.ScriptKind.JSX
        : /\.[cm]?js$/i.test(file) ? ts.ScriptKind.JS
        : ts.ScriptKind.TS

    // setParentNodes:false — we never need parent pointers (every helper takes `sf` explicitly),
    // and skipping them is a measurable win across ~650 files.
    const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, /*setParentNodes*/ false, scriptKind)

    /** @type {Decl[]} */
    const decls = []
    /** @type {Edge[]} */
    const edges = []
    /** @type {Set<string>} */
    const exportedNames = new Set()
    let hasJsx = false

    const lineOf = (pos) => sf.getLineAndCharacterOfPosition(pos).line + 1
    const src = (node) => text.slice(ts.skipTrivia(text, node.pos), node.end)
    const startOf = (node) => ts.skipTrivia(text, node.pos)

    /** First sentence of the nearest leading `/** … *\/`. Cheap doc without parent pointers. */
    const docOf = (node) => {
        const ranges = ts.getLeadingCommentRanges(text, node.pos)
        if (!ranges?.length) return ''
        const last = ranges[ranges.length - 1]
        const raw = text.slice(last.pos, last.end)
        if (!raw.startsWith('/**')) return ''
        const body = raw
            .replace(/^\/\*\*/, '')
            .replace(/\*\/$/, '')
            .split(/\r?\n/)
            .map((l) => l.replace(/^\s*\*\s?/, '').trim())
            .filter((l) => l && !l.startsWith('@'))
            .join(' ')
            .trim()
        const stop = body.search(/\.\s|\.$/)
        const first = stop > 0 ? body.slice(0, stop + 1) : body
        return first.length > 110 ? first.slice(0, 107) + '…' : first
    }

    const hasModifier = (node, kind) => node.modifiers?.some((m) => m.kind === kind) ?? false
    const isExported = (node) => hasModifier(node, ts.SyntaxKind.ExportKeyword)
    const isDefault = (node) => hasModifier(node, ts.SyntaxKind.DefaultKeyword)

    /** Does this subtree contain JSX? Decides component-vs-function without a checker. */
    const containsJsx = (node) => {
        let found = false
        const walk = (n) => {
            if (found || !n) return
            switch (n.kind) {
                case ts.SyntaxKind.JsxElement:
                case ts.SyntaxKind.JsxSelfClosingElement:
                case ts.SyntaxKind.JsxFragment:
                    found = true
                    return
            }
            ts.forEachChild(n, walk)
        }
        walk(node)
        return found
    }

    const paramSig = (node) => {
        const ps = node.parameters ? node.parameters.map((p) => src(p).replace(/\s+/g, ' ')) : []
        const ret = node.type ? `: ${src(node.type).replace(/\s+/g, ' ')}` : ''
        const tp = node.typeParameters?.length ? `<${node.typeParameters.map((t) => src(t)).join(', ')}>` : ''
        return `${tp}(${ps.join(', ')})${ret}`
    }

    /** @type {(d: Partial<Decl> & {name: string, kind: string, node: any}) => void} */
    const push = ({ name, kind, node, exported = false, isDefault: dflt = false, sig = '', container = '', params = 0 }) => {
        decls.push({
            name,
            kind,
            exported,
            isDefault: dflt,
            line: lineOf(startOf(node)),
            endLine: lineOf(node.end),
            pos: startOf(node.name ?? node),
            sig,
            container,
            doc: docOf(node),
            params,
        })
        if (exported) exportedNames.add(dflt ? 'default' : name)
    }

    // ---- members of a class / interface / type-literal ------------------------------------
    const memberKind = (m) => {
        if (ts.isMethodDeclaration(m) || ts.isMethodSignature(m)) return 'method'
        if (ts.isGetAccessor(m)) return 'getter'
        if (ts.isSetAccessor(m)) return 'setter'
        if (ts.isConstructorDeclaration(m)) return 'ctor'
        if (ts.isPropertyDeclaration(m) || ts.isPropertySignature(m)) return 'prop'
        return ''
    }

    const visibility = (m) => {
        if (hasModifier(m, ts.SyntaxKind.PrivateKeyword)) return 'private'
        if (hasModifier(m, ts.SyntaxKind.ProtectedKeyword)) return 'protected'
        if (m.name && ts.isPrivateIdentifier(m.name)) return 'private'
        return 'public'
    }

    const addMembers = (node, ownerName) => {
        for (const m of node.members ?? []) {
            const k = memberKind(m)
            if (!k) continue
            const nm = m.name ? src(m.name) : k === 'ctor' ? 'constructor' : '(anonymous)'
            const vis = visibility(m)
            const sig = k === 'method' || k === 'ctor'
                ? paramSig(m)
                : m.type ? `: ${src(m.type).replace(/\s+/g, ' ')}` : ''
            push({
                name: nm,
                kind: vis === 'public' ? k : `${vis} ${k}`,
                node: m,
                exported: false,
                sig,
                container: ownerName,
                params: m.parameters?.length ?? 0,
            })
        }
    }

    // ---- module edges ----------------------------------------------------------------------
    /** @type {(spec: any, names: string[], kind: Edge['kind'], node: any) => void} */
    const edge = (spec, names, kind, node) => {
        if (!spec || !ts.isStringLiteralLike(spec)) return
        edges.push({ spec: spec.text, names, line: lineOf(startOf(node)), kind })
    }

    const importedNames = (clause) => {
        if (!clause) return ['*']              // `import './x'` — a side-effect import needs the file
        /** @type {string[]} */
        const names = []
        if (clause.name) names.push('default')
        const b = clause.namedBindings
        if (b) {
            if (ts.isNamespaceImport(b)) names.push('*')
            else for (const el of b.elements) names.push((el.propertyName ?? el.name).text)
        }
        return names.length ? names : ['*']
    }

    // ---- top-level walk ---------------------------------------------------------------------
    for (const st of sf.statements) {
        // --- imports / re-exports
        if (ts.isImportDeclaration(st)) {
            edge(st.moduleSpecifier, importedNames(st.importClause), 'import', st)
            continue
        }
        if (ts.isImportEqualsDeclaration(st)) {
            const ref = st.moduleReference
            if (ts.isExternalModuleReference(ref)) edge(ref.expression, ['*'], 'require', st)
            continue
        }
        if (ts.isExportDeclaration(st)) {
            if (st.moduleSpecifier) {
                if (!st.exportClause) {
                    edge(st.moduleSpecifier, ['*'], 'star-reexport', st)
                } else if (ts.isNamespaceExport(st.exportClause)) {
                    edge(st.moduleSpecifier, ['*'], 'reexport', st)
                    exportedNames.add(st.exportClause.name.text)
                } else {
                    const names = st.exportClause.elements.map((e) => (e.propertyName ?? e.name).text)
                    edge(st.moduleSpecifier, names, 'reexport', st)
                    for (const e of st.exportClause.elements) exportedNames.add(e.name.text)
                }
            } else if (st.exportClause && ts.isNamedExports(st.exportClause)) {
                // `export { a, b as c }` — the names become part of the module's surface, and the
                // local decl they alias is reachable through them.
                for (const e of st.exportClause.elements) {
                    exportedNames.add(e.name.text)
                    const local = (e.propertyName ?? e.name).text
                    const target = decls.find((d) => d.name === local && !d.container)
                    if (target) target.exported = true
                }
            }
            continue
        }
        if (ts.isExportAssignment(st)) {
            exportedNames.add('default')
            const expr = st.expression
            if (ts.isIdentifier(expr)) {
                const target = decls.find((d) => d.name === expr.text && !d.container)
                if (target) {
                    target.exported = true
                    target.isDefault = true
                }
            }
            continue
        }

        // --- declarations
        const exported = isExported(st)
        const dflt = isDefault(st)

        if (ts.isFunctionDeclaration(st)) {
            const name = st.name?.text ?? (dflt ? 'default' : '(anonymous)')
            const jsx = containsJsx(st.body)
            if (jsx) hasJsx = true
            push({
                name,
                kind: classify(name, jsx),
                node: st,
                exported,
                isDefault: dflt,
                sig: paramSig(st),
                params: st.parameters.length,
            })
        } else if (ts.isClassDeclaration(st)) {
            const name = st.name?.text ?? (dflt ? 'default' : '(anonymous)')
            const heritage = (st.heritageClauses ?? []).map((h) => src(h).replace(/\s+/g, ' ')).join(' ')
            push({ name, kind: 'class', node: st, exported, isDefault: dflt, sig: heritage })
            addMembers(st, name)
        } else if (ts.isInterfaceDeclaration(st)) {
            const heritage = (st.heritageClauses ?? []).map((h) => src(h).replace(/\s+/g, ' ')).join(' ')
            push({ name: st.name.text, kind: 'interface', node: st, exported, sig: heritage })
            addMembers(st, st.name.text)
        } else if (ts.isTypeAliasDeclaration(st)) {
            const body = src(st.type).replace(/\s+/g, ' ')
            push({
                name: st.name.text,
                kind: 'type',
                node: st,
                exported,
                sig: body.length > 90 ? body.slice(0, 87) + '…' : body,
            })
            if (ts.isTypeLiteralNode(st.type)) addMembers(st.type, st.name.text)
        } else if (ts.isEnumDeclaration(st)) {
            push({ name: st.name.text, kind: 'enum', node: st, exported, sig: `{ ${st.members.length} members }` })
            for (const m of st.members) {
                push({ name: src(m.name), kind: 'enum-member', node: m, container: st.name.text })
            }
        } else if (ts.isModuleDeclaration(st)) {
            push({ name: src(st.name), kind: 'module', node: st, exported })
        } else if (ts.isVariableStatement(st)) {
            const kw = st.declarationList.flags & ts.NodeFlags.Const ? 'const'
                : st.declarationList.flags & ts.NodeFlags.Let ? 'let' : 'var'
            for (const d of st.declarationList.declarations) {
                if (!ts.isIdentifier(d.name)) {
                    // `const { a, b } = …` at module scope: each binding is its own export.
                    for (const el of bindingNames(ts, d.name)) {
                        push({ name: el, kind: kw, node: d, exported })
                    }
                    continue
                }
                const name = d.name.text
                const init = d.initializer
                const isFn = init && (ts.isArrowFunction(init) || ts.isFunctionExpression(init))
                const jsx = isFn ? containsJsx(init.body) : false
                if (jsx) hasJsx = true
                const kind = isFn ? classify(name, jsx)
                    : init && ts.isClassExpression(init) ? 'class'
                        : kw
                const sig = isFn ? paramSig(init)
                    : d.type ? `: ${src(d.type).replace(/\s+/g, ' ')}` : ''
                push({
                    name,
                    kind,
                    node: d,
                    exported,
                    sig,
                    params: isFn ? init.parameters.length : 0,
                })
            }
        }
    }

    // Dynamic edges live anywhere, so they need a full walk — but only over expression nodes.
    /** @type {Set<string>} */
    const directives = new Set()
    /**
     * How often each identifier appears in this file. Feeds `localRefs`, which is what separates
     * "delete this" from "just un-export it" — the single most useful distinction `dead --exports`
     * can draw, and one the module graph alone cannot make.
     *
     * Deliberately over-counts (a `obj.foo` property and a `type X`/`const X` pair both inflate it).
     * Over-counting only ever moves a finding from "delete" to "un-export", which is the safe
     * direction to be wrong in.
     * @type {Map<string, number>}
     */
    const idFreq = new Map()
    const walkDynamic = (n) => {
        if (n.kind === ts.SyntaxKind.Identifier && n.escapedText !== undefined) {
            const t = String(n.escapedText)
            idFreq.set(t, (idFreq.get(t) ?? 0) + 1)
        }
        // `use:autoresize` — a Solid directive. The imported function is referenced ONLY by this
        // attribute, so without collecting it here every directive reads as an unused import.
        if (n.kind === ts.SyntaxKind.JsxAttribute && n.name) {
            if (n.name.kind === ts.SyntaxKind.JsxNamespacedName) {
                if (n.name.namespace?.text === 'use') directives.add(n.name.name.text)
            } else {
                const raw = src(n.name)
                const m = raw.match(/^use:([A-Za-z_$][\w$]*)$/)
                if (m) directives.add(m[1])
            }
        }
        if (ts.isCallExpression(n)) {
            if (n.expression.kind === ts.SyntaxKind.ImportKeyword) {
                edge(n.arguments[0], ['*'], 'dynamic', n)
            } else if (ts.isIdentifier(n.expression) && n.expression.text === 'require') {
                edge(n.arguments[0], ['*'], 'require', n)
            } else if (
                ts.isPropertyAccessExpression(n.expression) &&
                n.expression.name.text === 'glob' &&
                /import\.meta/.test(src(n.expression.expression))
            ) {
                // Vite's import.meta.glob('./x/*.ts') pulls in files nothing names explicitly.
                for (const arg of n.arguments) {
                    if (ts.isStringLiteralLike(arg)) edge(arg, ['*'], 'glob', n)
                    else if (ts.isArrayLiteralExpression(arg)) {
                        for (const el of arg.elements) if (ts.isStringLiteralLike(el)) edge(el, ['*'], 'glob', n)
                    }
                }
            }
        }
        if (!hasJsx && (n.kind === ts.SyntaxKind.JsxElement || n.kind === ts.SyntaxKind.JsxSelfClosingElement || n.kind === ts.SyntaxKind.JsxFragment)) {
            hasJsx = true
        }
        ts.forEachChild(n, walkDynamic)
    }
    ts.forEachChild(sf, walkDynamic)

    // Charge each declaration with the uses of its own name inside this file. One subtracted for
    // the declaration's own identifier.
    for (const d of decls) {
        d.localRefs = Math.max(0, (idFreq.get(d.name) ?? 0) - 1)
    }

    return {
        file,
        decls,
        edges,
        exportedNames: [...exportedNames],
        directives: [...directives],
        loc: sf.getLineAndCharacterOfPosition(sf.end).line + 1,
        hasJsx,
    }
}

/** Names bound by a destructuring pattern, flattened. */
function bindingNames(ts, pattern) {
    /** @type {string[]} */
    const names = []
    const walk = (p) => {
        for (const el of p.elements ?? []) {
            if (!el.name) continue
            if (ts.isIdentifier(el.name)) names.push(el.name.text)
            else walk(el.name)
        }
    }
    walk(pattern)
    return names
}

/**
 * Component / hook / primitive / plain function. Convention-based on purpose: in a Solid or React
 * codebase the naming convention IS the contract, and no amount of type checking recovers the
 * author's intent better than `PascalCase + returns JSX`.
 */
function classify(name, hasJsx) {
    if (hasJsx && COMPONENT_NAME.test(name)) return 'component'
    if (HOOK_NAME.test(name)) return 'hook'
    if (PRIMITIVE_NAME.test(name)) return 'primitive'
    return 'fn'
}

/** Kind groups accepted by `--kind`, so `--kind type` means "any type-ish declaration". */
const KIND_ALIASES = {
    type: ['type', 'interface', 'enum', 'class'],
    fn: ['fn', 'hook', 'primitive', 'component'],
    func: ['fn', 'hook', 'primitive', 'component'],
    function: ['fn', 'hook', 'primitive', 'component'],
    comp: ['component'],
    var: ['const', 'let', 'var'],
    member: ['method', 'prop', 'getter', 'setter', 'ctor', 'enum-member'],
    field: ['prop'],
}

/** @param {string[]|undefined} kinds */
export function kindFilter(kinds) {
    if (!kinds?.length) return () => true
    /** @type {Set<string>} */
    const want = new Set()
    for (const k of kinds) {
        const expanded = KIND_ALIASES[k] ?? [k]
        for (const e of expanded) want.add(e)
    }
    // `private method` should match `--kind method`.
    return (kind) => want.has(kind) || want.has(kind.replace(/^(private|protected) /, ''))
}
