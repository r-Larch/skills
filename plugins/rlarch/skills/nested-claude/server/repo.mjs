// repo.mjs — the repository identity a WorkState project is bound to (docs/workstate/02 project_repo).
//
// The same rule as RepoKeys in mcp/WorkState.Core/Domain/Repos.cs; both run the table in
// docs/workstate/repo-keys.json. The server normalises too, so this only has to compute the keys.
// @ts-check

import path from 'node:path'
import { spawnSync } from 'node:child_process'

/**
 * `git@github.com:r-Larch/skills.git` and `https://github.com/r-Larch/skills` are both
 * `github.com/r-larch/skills`.
 * @param {string|null|undefined} remote
 * @returns {string|null}
 */
export function normaliseRemote(remote) {
    let value = (remote ?? '').trim().replace(/\\/g, '/')
    if (!value) return null
    const scheme = /^[a-z][a-z0-9+.-]*:\/\//i
    const schemed = scheme.test(value)
    value = value.replace(scheme, '').replace(/^[^/]*@/, '')
    // scp form host:owner/name; a one-letter host is a drive letter and never matches.
    if (!schemed) value = value.replace(/^([^/:]{2,}):\/*/, '$1/')
    value = value.replace(/\/*(\.git)?\/*$/i, '')
    return value ? value.toLowerCase() : null
}

/**
 * The main checkout's root: forward slashes, no trailing slash or /.git, lowercase on Windows.
 * @param {string|null|undefined} root
 * @returns {string|null}
 */
export function normaliseRoot(root) {
    let value = (root ?? '').trim().replace(/\\/g, '/')
    if (!value) return null
    value = value.replace(/\/*(\/\.git)?\/*$/i, '')
    if (/^([a-z]:(\/|$)|\/\/)/i.test(value)) value = value.toLowerCase()
    return value || null
}

/** @param {string} cwd @param {string[]} args */
function git(cwd, args) {
    const r = spawnSync('git', ['-C', cwd, ...args], { encoding: 'utf8', windowsHide: true })
    return r.status === 0 ? r.stdout.trim() : null
}

/**
 * The keys of the repository `repoRoot` belongs to. The root is the parent of the common git dir,
 * so every worktree of a repo yields its main checkout's root; a repo with no origin has no remote.
 * @param {string} repoRoot
 * @returns {{remote?: string, root?: string}}
 */
export function repoKeys(repoRoot) {
    /** @type {{remote?: string, root?: string}} */
    const keys = {}
    const remote = normaliseRemote(git(repoRoot, ['remote', 'get-url', 'origin']))
    if (remote) keys.remote = remote
    const common = git(repoRoot, ['rev-parse', '--path-format=absolute', '--git-common-dir'])
    const root = common ? normaliseRoot(path.dirname(common)) : null
    if (root) keys.root = root
    return keys
}
