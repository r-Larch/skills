// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { startServer, makeRepo, fakeWorkstate, CONTROLLER, tmp } from './helpers.mjs'
import { normaliseRemote, normaliseRoot, repoKeys } from '../repo.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const TABLE = path.join(here, '..', '..', '..', '..', '..', '..', 'docs', 'workstate', 'repo-keys.json')

/** @param {string} cwd @param {string[]} args */
const git = (cwd, ...args) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@t', ...args], { cwd, stdio: 'pipe' })

/** A repo with one commit (a worktree needs one) and an origin remote. */
function repoWithOrigin() {
    const repo = makeRepo()
    git(repo.root, 'add', '.gitignore', 'task.md')
    git(repo.root, 'commit', '-q', '-m', 'init')
    git(repo.root, 'remote', 'add', 'origin', 'git@github.com:r-Larch/Fixture.git')
    return repo
}

test('every row of the identity table normalises as the server does', () => {
    const table = JSON.parse(fs.readFileSync(TABLE, 'utf8'))
    for (const [kind, normalise] of /** @type {const} */ ([['remote', normaliseRemote], ['root', normaliseRoot]])) {
        assert.ok(table[kind].length > 0, `${kind} rows`)
        for (const [input, expected] of table[kind]) {
            assert.equal(normalise(input), expected, `${kind} ${JSON.stringify(input)}`)
        }
    }
})

test('a repo with no origin has a root key only', () => {
    const repo = makeRepo()
    assert.deepEqual(repoKeys(repo.root), { root: normaliseRoot(fs.realpathSync.native(repo.root)) })
})

test('a worktree and its main checkout yield the same keys: the remote and the main root', () => {
    const repo = repoWithOrigin()
    const worktree = path.join(tmp('wt'), 'unit')
    git(repo.root, 'worktree', 'add', '-q', '-b', 'unit', worktree)

    const main = repoKeys(repo.root)
    assert.deepEqual(main, { remote: 'github.com/r-larch/fixture', root: normaliseRoot(fs.realpathSync.native(repo.root)) })
    assert.deepEqual(repoKeys(worktree), main)
    assert.notEqual(normaliseRoot(worktree), main.root)
})

test('agent_launch from a worktree mints with the remote and the main root', async () => {
    const repo = repoWithOrigin()
    const worktree = path.join(tmp('wt'), 'launch')
    git(repo.root, 'worktree', 'add', '-q', '-b', 'launch', worktree)
    const ws = await fakeWorkstate()
    const s = startServer()
    try {
        const r = await s.call('agent_launch', {
            name: 'unit-repo', brief: 'task.md', repo_root: worktree, controller: CONTROLLER,
            workstate: { env_file: repo.envFile, url: ws.url },
        })
        assert.equal(r.isError, false, r.content[0].text)
        const mint = ws.requests.find((q) => q.method === 'POST')
        assert.deepEqual(mint?.body.repo, { remote: 'github.com/r-larch/fixture', root: normaliseRoot(fs.realpathSync.native(repo.root)) })
        assert.equal(mint?.body.cwd, worktree)
    } finally {
        await s.close()
        await ws.close()
    }
})
