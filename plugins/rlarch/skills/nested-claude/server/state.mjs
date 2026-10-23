// state.mjs — one JSON file per launched agent, so agent_stop and agent_list survive a restart.
// Holds paths and ids only: the env file is recorded by PATH and no token is ever written here.
// @ts-check

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

/**
 * @typedef {object} AgentRecord
 * @property {string} name
 * @property {string|undefined} id          claude's short id
 * @property {string|undefined} sessionId   claude's session uuid
 * @property {string} repo_root
 * @property {string} brief
 * @property {string|undefined} mcpConfigPath
 * @property {string|undefined} envFile
 * @property {string|undefined} url
 * @property {{sessionId: string, role: string, expiresAt: string|undefined}|null} workstate
 * @property {string} launchedAt
 */

/** @param {NodeJS.ProcessEnv} [env] */
export function stateDir(env = process.env) {
    const base = env.CLAUDE_PLUGIN_DATA || path.join(os.tmpdir(), 'rlarch-agents')
    const dir = path.join(base, 'agents')
    fs.mkdirSync(dir, { recursive: true })
    return dir
}

/** @param {string} name */
function fileFor(name) {
    return path.join(stateDir(), `${name}.json`)
}

/** @param {AgentRecord} rec */
export function saveRecord(rec) {
    fs.writeFileSync(fileFor(rec.name), JSON.stringify(rec, null, 2) + '\n')
}

/** @returns {AgentRecord[]} */
export function loadRecords() {
    const dir = stateDir()
    /** @type {AgentRecord[]} */
    const out = []
    for (const f of fs.readdirSync(dir)) {
        if (!f.endsWith('.json')) continue
        try {
            out.push(JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')))
        } catch {}
    }
    return out
}

/** @param {{id?: string, name?: string}} key */
export function findRecord(key) {
    return loadRecords().find((r) => (key.name !== undefined && r.name === key.name) || (key.id !== undefined && r.id === key.id))
}

/** @param {string} name */
export function deleteRecord(name) {
    fs.rmSync(fileFor(name), { force: true })
}
