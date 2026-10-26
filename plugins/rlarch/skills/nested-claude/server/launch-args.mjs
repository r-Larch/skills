// launch-args.mjs — validate agent_launch input and turn it into the claude argv.
// @ts-check

import fs from 'node:fs'
import path from 'node:path'
import { toolsetProfile } from './header.mjs'

/** A refusal the caller can act on. Its message says what to fix; it never carries a secret. */
export class Refusal extends Error {}

export const PERMISSION_MODES = ['acceptEdits', 'auto', 'bypassPermissions', 'manual', 'dontAsk', 'plan']
export const TOOLSETS = ['none', 'readonly', 'fix']
/** Every tool of the server named "workstate" in mcp-<name>.json. */
export const WORKSTATE_TOOLS = 'mcp__workstate'
export const EFFORTS = ['low', 'medium', 'high', 'xhigh', 'max']
export const ROLES = ['worker', 'orchestrator', 'controller', 'none']
export const DEFAULT_WORKSTATE_URL = 'http://127.0.0.1:5290'

const LAUNCH_KEYS = [
    'name', 'brief', 'repo_root', 'controller', 'permission_mode', 'toolset', 'allowed_tools',
    'disallowed_tools', 'add_dirs', 'model', 'effort', 'settings', 'append_system_prompt',
    'workstate', 'dry_run',
]
const WORKSTATE_KEYS = ['role', 'env_file', 'url', 'ttl_hours']

const NAME_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/
const CONTROLLER_RE = /^\S.*\s\[[0-9a-fA-F]{6}\]$/
// The prompt names the brief path, and a positional prompt is cut at its first quote (SKILL.md §1).
const QUOTE_RE = /['"`]/

/**
 * @typedef {object} WorkstateParams
 * @property {'worker'|'orchestrator'|'controller'|'none'} role
 * @property {string|undefined} envFile
 * @property {string} url
 * @property {number|undefined} ttlHours
 *
 * @typedef {object} LaunchParams
 * @property {string} name
 * @property {string} brief        absolute path of the caller's brief
 * @property {string} repoRoot
 * @property {string} controller
 * @property {string} permissionMode
 * @property {'none'|'readonly'|'fix'} toolset
 * @property {string[]} allowedTools  profile merged with the caller's, first occurrence wins
 * @property {string[]} disallowedTools
 * @property {string[]} addDirs
 * @property {string|undefined} model
 * @property {string|undefined} effort
 * @property {string|undefined} settings
 * @property {string|undefined} appendSystemPrompt
 * @property {WorkstateParams} workstate
 * @property {boolean} dryRun
 */

/** @param {unknown} v */
function isObject(v) {
    return typeof v === 'object' && v !== null && !Array.isArray(v)
}

/**
 * @param {Record<string, unknown>} obj
 * @param {string[]} allowed
 * @param {string} where
 */
function rejectUnknown(obj, allowed, where) {
    const extra = Object.keys(obj).filter((k) => !allowed.includes(k))
    if (extra.length) throw new Refusal(`${where}: unknown parameter(s) ${extra.join(', ')}; allowed: ${allowed.join(', ')}`)
}

/**
 * A value claude would read as a flag instead of the value of the flag before it.
 * @param {string} v
 * @param {string} param
 */
function notFlagLike(v, param) {
    if (v.startsWith('-')) throw new Refusal(`${param}: "${v}" starts with "-", which claude would parse as a flag`)
    return v
}

/**
 * @param {unknown} v
 * @param {string} param
 * @returns {string|undefined}
 */
function optString(v, param) {
    if (v === undefined || v === null) return undefined
    if (typeof v !== 'string' || v.trim() === '') throw new Refusal(`${param} must be a non-empty string`)
    return notFlagLike(v, param)
}

/**
 * @param {unknown} v
 * @param {string} param
 * @returns {string[]}
 */
function stringArray(v, param) {
    if (v === undefined || v === null) return []
    if (!Array.isArray(v)) throw new Refusal(`${param} must be an array of strings`)
    return v.map((x, i) => {
        if (typeof x !== 'string' || x.trim() === '') throw new Refusal(`${param}[${i}] must be a non-empty string`)
        return notFlagLike(x, `${param}[${i}]`)
    })
}

/**
 * @param {unknown} v
 * @param {string[]} values
 * @param {string} param
 * @param {string|undefined} fallback
 */
function oneOf(v, values, param, fallback) {
    if (v === undefined || v === null) return fallback
    if (typeof v !== 'string' || !values.includes(v)) throw new Refusal(`${param} must be one of ${values.join(', ')} (got ${JSON.stringify(v)})`)
    return v
}

/** @param {string} p */
function realpath(p) {
    return fs.realpathSync.native(p)
}

/** @param {string} child @param {string} parent */
function isInside(child, parent) {
    const rel = path.relative(parent, child)
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel)
}

/**
 * Validate and normalise agent_launch input. Throws Refusal; reads the disk but writes nothing.
 * @param {unknown} input
 * @returns {LaunchParams}
 */
export function validateLaunch(input) {
    if (!isObject(input)) throw new Refusal('agent_launch takes an object of parameters')
    const a = /** @type {Record<string, unknown>} */ (input)
    rejectUnknown(a, LAUNCH_KEYS, 'agent_launch')

    if (typeof a.name !== 'string' || !NAME_RE.test(a.name)) {
        throw new Refusal('name is required: 1-64 characters of letters, digits, ".", "_" or "-", starting with a letter or digit')
    }
    const name = a.name

    if (typeof a.repo_root !== 'string' || a.repo_root.trim() === '') throw new Refusal('repo_root is required: the absolute path of the git working tree the agent works in')
    if (!path.isAbsolute(a.repo_root)) throw new Refusal(`repo_root must be an absolute path (got ${a.repo_root})`)
    if (QUOTE_RE.test(a.repo_root)) throw new Refusal('repo_root must not contain a quote character: the prompt names a path inside it, and claude cuts a prompt at its first quote')
    if (!fs.existsSync(a.repo_root) || !fs.statSync(a.repo_root).isDirectory()) {
        throw new Refusal(`repo_root does not exist: ${a.repo_root}. Did git worktree add fail?`)
    }
    if (!fs.existsSync(path.join(a.repo_root, '.git'))) {
        throw new Refusal(`repo_root is not a git working tree: ${a.repo_root}. A --bg agent must be launched inside one - see nested-claude SKILL.md section 7.`)
    }
    const repoRoot = path.resolve(a.repo_root)

    if (typeof a.controller !== 'string' || !CONTROLLER_RE.test(a.controller) || /[\r\n]/.test(a.controller)) {
        throw new Refusal("controller must include the ref, e.g. 'my-session [9825b9]'. Run ListAgents and copy the first line.")
    }
    const controller = a.controller

    if (typeof a.brief !== 'string' || a.brief.trim() === '') throw new Refusal('brief is required: the path of a file inside repo_root')
    const brief = path.resolve(repoRoot, a.brief)
    if (!fs.existsSync(brief) || !fs.statSync(brief).isFile()) throw new Refusal(`brief not found: ${brief}`)
    if (!isInside(realpath(brief), realpath(repoRoot))) {
        throw new Refusal(`brief must be a file inside repo_root (${repoRoot}); got ${brief}`)
    }

    const permissionMode = /** @type {string} */ (oneOf(a.permission_mode, PERMISSION_MODES, 'permission_mode', 'auto'))
    const toolset = /** @type {'none'|'readonly'|'fix'} */ (oneOf(a.toolset, TOOLSETS, 'toolset', 'fix'))
    const workstate = validateWorkstate(a.workstate, repoRoot)
    // A minted session must reach its own server without a prompt: under acceptEdits an MCP call
    // parks the agent. The server, not this list, limits which work_* tools the role sees.
    const workstateTools = workstate.role === 'none' ? [] : [WORKSTATE_TOOLS]
    const allowedTools = [...new Set([...toolsetProfile(toolset), ...workstateTools, ...stringArray(a.allowed_tools, 'allowed_tools')])]
    const disallowedTools = stringArray(a.disallowed_tools, 'disallowed_tools')
    const addDirs = stringArray(a.add_dirs, 'add_dirs')
    const model = optString(a.model, 'model')
    const effort = oneOf(a.effort, EFFORTS, 'effort', undefined)
    const appendSystemPrompt = optString(a.append_system_prompt, 'append_system_prompt')

    let settings = optString(a.settings, 'settings')
    if (settings !== undefined) {
        if (settings.trimStart().startsWith('{')) {
            try {
                JSON.parse(settings)
            } catch (e) {
                throw new Refusal(`settings looks like JSON but does not parse: ${/** @type {Error} */ (e).message}`)
            }
        } else {
            settings = path.resolve(repoRoot, settings)
            if (!fs.existsSync(settings)) throw new Refusal(`settings file not found: ${settings}`)
        }
    }

    if (a.dry_run !== undefined && typeof a.dry_run !== 'boolean') throw new Refusal('dry_run must be true or false')

    return {
        name,
        brief,
        repoRoot,
        controller,
        permissionMode,
        toolset,
        allowedTools,
        disallowedTools,
        addDirs,
        model,
        effort,
        settings,
        appendSystemPrompt,
        workstate,
        dryRun: a.dry_run === true,
    }
}

/**
 * @param {unknown} v
 * @param {string} repoRoot
 * @returns {WorkstateParams}
 */
function validateWorkstate(v, repoRoot) {
    if (v === undefined || v === null) v = {}
    if (!isObject(v)) throw new Refusal('workstate must be an object: {role, env_file, url?, ttl_hours?}')
    const w = /** @type {Record<string, unknown>} */ (v)
    rejectUnknown(w, WORKSTATE_KEYS, 'workstate')

    const role = /** @type {WorkstateParams['role']} */ (oneOf(w.role, ROLES, 'workstate.role', 'worker'))

    let url = DEFAULT_WORKSTATE_URL
    if (w.url !== undefined && w.url !== null) {
        if (typeof w.url !== 'string' || !/^https?:\/\/[^\s/]+/.test(w.url)) throw new Refusal('workstate.url must be an http(s) URL, e.g. http://127.0.0.1:5290')
        url = w.url.replace(/\/+$/, '')
    }

    let ttlHours
    if (w.ttl_hours !== undefined && w.ttl_hours !== null) {
        if (typeof w.ttl_hours !== 'number' || !Number.isInteger(w.ttl_hours) || w.ttl_hours < 1 || w.ttl_hours > 720) {
            throw new Refusal('workstate.ttl_hours must be an integer from 1 to 720')
        }
        ttlHours = w.ttl_hours
    }

    let envFile
    if (w.env_file !== undefined && w.env_file !== null) {
        if (typeof w.env_file !== 'string' || w.env_file.trim() === '') throw new Refusal('workstate.env_file must be a path')
        envFile = path.resolve(repoRoot, w.env_file)
    }
    if (role !== 'none' && envFile === undefined) {
        throw new Refusal(`workstate.role is "${role}", so a per-session token must be minted, and that needs workstate.env_file: the PATH of the WorkState clone's mcp/.env (never its contents). Pass workstate.role "none" to launch without WorkState.`)
    }

    return { role, envFile, url, ttlHours }
}

/** @param {string} briefPath */
export function promptLine(briefPath) {
    return `Read the file ${briefPath} and follow it exactly. It is your brief. Start now.`
}

/**
 * The claude argv after the executable.
 *
 * --allowedTools, --disallowedTools, --add-dir and --mcp-config are variadic: each would swallow the
 * prompt if it came last. Each is closed by the flag after it, the single-value flags follow, and
 * --permission-mode always comes immediately before the prompt. Reorder this and the prompt vanishes.
 *
 * @param {LaunchParams} p
 * @param {{briefPath: string, mcpConfigPath: string|undefined}} files
 */
export function buildArgv(p, files) {
    const argv = ['--bg', '--name', p.name]
    if (p.allowedTools.length) argv.push('--allowedTools', ...p.allowedTools)
    if (p.disallowedTools.length) argv.push('--disallowedTools', ...p.disallowedTools)
    if (p.addDirs.length) argv.push('--add-dir', ...p.addDirs)
    if (files.mcpConfigPath) argv.push('--mcp-config', files.mcpConfigPath)
    if (p.model) argv.push('--model', p.model)
    if (p.effort) argv.push('--effort', p.effort)
    if (p.settings) argv.push('--settings', p.settings)
    if (p.appendSystemPrompt) argv.push('--append-system-prompt', p.appendSystemPrompt)
    argv.push('--permission-mode', p.permissionMode)
    argv.push(promptLine(files.briefPath))
    return argv
}
