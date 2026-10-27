#!/usr/bin/env node
// find-shadows.mjs — lists every registration of the WorkState server that wins over the user-scope
// one: a local-scope entry in the claude config (projects[<dir>].mcpServers[<name>]) and a
// project-scope entry in <dir>/.mcp.json. Claude Code resolves local > project > user, so a session in
// such a directory never sees the user registration or its headersHelper.
//   node find-shadows.mjs [<claude config>] [--name <server>] [--dir <dir>] [--own <dir>]
// <claude config> defaults to $CLAUDE_CONFIG_DIR/.claude.json, else ~/.claude.json. --dir also checks
// that directory's .mcp.json. --own skips the local entry of that directory (the one -Agent wrote).
// One line per shadowing entry with the command that removes it; nothing when none shadows; exit 0.
// Header values are never read into the output: only the url (without credentials or query) and
// whether headersHelper is set. A parse error is not printed either, because it quotes the file.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

/** @typedef {{scope: 'local' | 'project', dir: string, file: string, url: string, helper: boolean}} Shadow */

/** @param {string} dir */
export function sameDir(dir) {
    const d = dir.replace(/\\/g, '/').replace(/\/+$/, '')
    return process.platform === 'win32' ? d.toLowerCase() : d
}

/** The url with any userinfo, query and fragment dropped. @param {unknown} raw */
export function safeUrl(raw) {
    if (typeof raw !== 'string') return '(no url)'
    try {
        const u = new URL(raw)
        return `${u.protocol}//${u.host}${u.pathname}`
    } catch {
        return '(unparseable url)'
    }
}

/** @param {unknown} entry @returns {{url: string, helper: boolean}} */
function describe(entry) {
    const e = /** @type {any} */ (entry)
    return { url: safeUrl(e?.url), helper: typeof e?.headersHelper === 'string' && e.headersHelper.trim() !== '' }
}

/** @param {unknown} servers @param {string} name */
function entryOf(servers, name) {
    return servers && typeof servers === 'object' && Object.hasOwn(servers, name) ? /** @type {any} */ (servers)[name] : undefined
}

/**
 * @param {unknown} config the parsed claude config
 * @param {string} name
 * @param {string} configFile
 * @param {string} [own] a directory whose local entry is the one just written, not a shadow
 * @returns {Shadow[]}
 */
export function localShadows(config, name, configFile, own) {
    const projects = /** @type {any} */ (config)?.projects
    if (!projects || typeof projects !== 'object') return []
    /** @type {Shadow[]} */
    const found = []
    for (const dir of Object.keys(projects)) {
        if (own && sameDir(dir) === sameDir(own)) continue
        const entry = entryOf(projects[dir]?.mcpServers, name)
        if (entry === undefined) continue
        found.push({ scope: 'local', dir, file: configFile, ...describe(entry) })
    }
    return found
}

/** @param {unknown} mcpJson the parsed .mcp.json @param {string} name @param {string} dir @param {string} file @returns {Shadow[]} */
export function projectShadows(mcpJson, name, dir, file) {
    const entry = entryOf(/** @type {any} */ (mcpJson)?.mcpServers, name)
    return entry === undefined ? [] : [{ scope: 'project', dir, file, ...describe(entry) }]
}

/** @param {Shadow} s @param {string} name */
export function formatShadow(s, name) {
    const helper = s.helper ? 'has headersHelper' : 'no headersHelper'
    const fix = s.scope === 'local'
        ? `in that directory run: claude mcp remove ${name} -s local`
        : `it is mcpServers.${name} in the committed team file ${s.file} - do not delete the file; agree with the repo to drop that entry (claude mcp remove ${name} -s project, run in that directory, edits it)`
    return `${s.scope.padEnd(8)}${s.dir}  ${s.url}  ${helper}  -> ${fix}`
}

/** @param {string} file @returns {{ok: true, value: unknown} | {ok: false, why: string}} */
function readJson(file) {
    let text
    try {
        text = fs.readFileSync(file, 'utf8')
    } catch (e) {
        return { ok: false, why: /** @type {any} */ (e)?.code === 'ENOENT' ? 'missing' : 'not readable' }
    }
    try {
        return { ok: true, value: JSON.parse(text.replace(/^﻿/, '')) }
    } catch {
        return { ok: false, why: 'not valid JSON' }
    }
}

/** @param {string[]} argv @returns {string[]} the lines to print */
export function findShadows(argv) {
    /** @type {{config?: string, name: string, dir?: string, own?: string}} */
    const opt = { name: 'workstate' }
    for (let i = 0; i < argv.length; i++) {
        const a = argv[i]
        if (a === '--name') opt.name = argv[++i] ?? opt.name
        else if (a === '--dir') opt.dir = argv[++i]
        else if (a === '--own') opt.own = argv[++i]
        else if (!opt.config) opt.config = a
    }
    const configFile = opt.config
        ?? path.join(process.env.CLAUDE_CONFIG_DIR || os.homedir(), '.claude.json')
    /** @type {string[]} */
    const lines = []
    const config = readJson(configFile)
    if (config.ok) {
        for (const s of localShadows(config.value, opt.name, configFile, opt.own)) lines.push(formatShadow(s, opt.name))
    } else {
        lines.push(`not checked: ${configFile} is ${config.why}`)
    }
    if (opt.dir) {
        const mcpFile = path.join(opt.dir, '.mcp.json')
        if (fs.existsSync(mcpFile)) {
            const mcp = readJson(mcpFile)
            if (mcp.ok) for (const s of projectShadows(mcp.value, opt.name, opt.dir, mcpFile)) lines.push(formatShadow(s, opt.name))
            else lines.push(`not checked: ${mcpFile} is ${mcp.why}`)
        }
    }
    return lines
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        for (const line of findShadows(process.argv.slice(2))) process.stdout.write(`${line}\n`)
    } catch {
        process.stdout.write('not checked: the shadow check failed\n')
    }
    process.exit(0)
}
