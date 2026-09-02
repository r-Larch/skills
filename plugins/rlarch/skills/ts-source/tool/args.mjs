// args.mjs — flag parsing. `--flag value`, `--flag=value`, bare `--flag` booleans, positionals.
// @ts-check

import { UserError } from './common.mjs'

/** Flags that take no value; anything else consumes the next token. */
const BOOLEAN = new Set([
    'regex', 'exported', 'json', 'all', 'files', 'exports', 'locals', 'deps',
    'include-public', 'include-generated', 'include-tests', 'callers', 'callees',
    'derived', 'importers', 'imports', 'semantic', 'stop', 'foreground', 'quiet', 'no-color',
])

export class Args {
    /** @param {string[]} argv */
    constructor(argv) {
        /** @type {string[]} */
        this.positional = []
        /** @type {Record<string, string|boolean>} */
        this.flags = {}

        for (let i = 0; i < argv.length; i++) {
            const t = argv[i]
            if (!t.startsWith('--')) {
                this.positional.push(t)
                continue
            }
            const eq = t.indexOf('=')
            if (eq > 0) {
                this.flags[t.slice(2, eq)] = t.slice(eq + 1)
                continue
            }
            const name = t.slice(2)
            if (BOOLEAN.has(name)) {
                this.flags[name] = true
            } else {
                const v = argv[i + 1]
                if (v === undefined || v.startsWith('--')) {
                    throw new UserError(`--${name} needs a value`)
                }
                this.flags[name] = v
                i++
            }
        }
    }

    /** @returns {string|undefined} */
    str(name) {
        const v = this.flags[name]
        return typeof v === 'string' ? v : undefined
    }

    bool(name) {
        return this.flags[name] === true
    }

    int(name, fallback) {
        const v = this.str(name)
        if (v === undefined) return fallback
        const n = Number(v)
        if (!Number.isFinite(n)) throw new UserError(`--${name} must be a number, got "${v}"`)
        return n
    }

    /** Comma-separated list flag: `--kind class,interface`. */
    list(name) {
        const v = this.str(name)
        return v === undefined ? undefined : v.split(',').map((s) => s.trim()).filter(Boolean)
    }

    /** Repeatable-ish: the first positional, or undefined. */
    first() {
        return this.positional[0]
    }

    require(index, what) {
        const v = this.positional[index]
        if (v === undefined) throw new UserError(`missing argument: ${what}`)
        return v
    }
}
