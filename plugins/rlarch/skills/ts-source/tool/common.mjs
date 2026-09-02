// common.mjs — output sink, errors, formatting, cache paths.
//
// Every command writes through `out()` rather than console.log, because the daemon serves one
// request at a time by swapping the sink to a buffer. Writing straight to stdout would send a
// daemon-handled result to the daemon's own terminal instead of the caller's.
// @ts-check

import os from 'node:os'
import path from 'node:path'
import fs from 'node:fs'
import crypto from 'node:crypto'

/** A message meant for the user, not a stack trace. Caught in main.mjs and printed as `error: …`. */
export class UserError extends Error {}

/** @type {((s: string) => void)|null} */
let sink = null

/** Redirect output into a buffer (the daemon). Returns a restore function. */
export function captureOutput() {
    /** @type {string[]} */
    const buf = []
    const prev = sink
    sink = (s) => buf.push(s)
    return () => {
        sink = prev
        return buf.join('')
    }
}

/** Write one line to the result stream. */
export function out(line = '') {
    if (sink) sink(line + '\n')
    else process.stdout.write(line + '\n')
}

/** Write to the *diagnostic* stream. Never stdout: stdout is the greppable result stream. */
export function note(line) {
    process.stderr.write(line + '\n')
}

/**
 * Print rows as aligned columns. Cells are padded to the widest value in each column except the
 * last, which is left ragged so long signatures don't force a wall of spaces.
 * @param {string[][]} rows
 */
export function table(rows) {
    if (rows.length === 0) return
    const cols = Math.max(...rows.map((r) => r.length))
    const w = []
    for (let c = 0; c < cols - 1; c++) {
        w[c] = Math.max(...rows.map((r) => (r[c] ?? '').length))
    }
    for (const r of rows) {
        const parts = []
        for (let c = 0; c < r.length; c++) {
            parts.push(c < cols - 1 ? (r[c] ?? '').padEnd(w[c]) : (r[c] ?? ''))
        }
        out(parts.join('  ').trimEnd())
    }
}

/** Trailer line carrying counts and timings, in the `// …` form the other tools use. */
export function trailer(text, startedAt) {
    const ms = Date.now() - startedAt
    out('')
    out(`// ${text} — ${ms} ms`)
}

/** Forward slashes everywhere in output, so `file:line` is clickable on every platform. */
export function slash(p) {
    return p.split(path.sep).join('/')
}

/** Case-insensitive, separator-normalised key for a path. Windows needs both. */
export function key(p) {
    return path.resolve(p).split(path.sep).join('/').toLowerCase()
}

/** `file:line` relative to the project root. */
export function loc(root, file, line) {
    return `${slash(path.relative(root, file)) || slash(file)}:${line}`
}

export function cacheRoot() {
    return process.env.TS_SOURCE_CACHE || path.join(os.tmpdir(), 'ts-source')
}

export function sha(input) {
    return crypto.createHash('sha256').update(input).digest('hex').slice(0, 16)
}

export function ensureDir(p) {
    fs.mkdirSync(p, { recursive: true })
    return p
}

/**
 * Glob -> RegExp for the subset we need (`*`, `**`, `?`). Anchored, separator-insensitive.
 * Used by --entry and --filter, never for module resolution (that is the compiler's job).
 */
export function globToRegExp(glob) {
    let re = ''
    const g = glob.split(path.sep).join('/')
    for (let i = 0; i < g.length; i++) {
        const c = g[i]
        if (c === '*') {
            if (g[i + 1] === '*') {
                re += '.*'
                i++
                if (g[i + 1] === '/') i++
            } else re += '[^/]*'
        } else if (c === '?') re += '[^/]'
        else re += c.replace(/[.+^${}()|[\]\\]/g, '\\$&')
    }
    return new RegExp('^' + re + '$', 'i')
}

/** Build a matcher from a user pattern: substring by default, regex with --regex. */
export function matcher(pattern, { regex = false } = {}) {
    if (!pattern) return () => true
    if (regex) {
        const re = new RegExp(pattern, 'i')
        return (s) => re.test(s)
    }
    const needle = pattern.toLowerCase()
    // A bare `*` reads as a glob to most people; support it without forcing --regex.
    if (needle.includes('*') || needle.includes('?')) {
        const re = globToRegExp(needle)
        return (s) => re.test(s)
    }
    return (s) => s.toLowerCase().includes(needle)
}
