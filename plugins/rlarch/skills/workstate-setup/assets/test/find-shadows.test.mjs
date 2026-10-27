// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const FINDER = path.join(HERE, '..', 'find-shadows.mjs')
const FIXTURES = path.join(HERE, 'fixtures', 'shadows')
const CONFIG = path.join(FIXTURES, 'claude.json')
const REPO = path.join(FIXTURES, 'repo')

/** Run the finder as the setup scripts do. @param {string[]} args */
function finder(args) {
    const r = spawnSync(process.execPath, [FINDER, ...args], { encoding: 'utf8' })
    assert.equal(r.status, 0, r.stderr)
    return { stdout: r.stdout, stderr: r.stderr, lines: r.stdout.split('\n').filter(Boolean) }
}

/** @param {string[]} lines @param {string} dir */
const lineFor = (lines, dir) => lines.find((l) => l.includes(`${dir}  `))

test('a local entry without headersHelper is reported with its remove command', () => {
    const line = lineFor(finder([CONFIG]).lines, 'P:/Projects/LarchSys/Skills')
    assert.ok(line, 'the Skills local entry is reported')
    assert.match(line, /^local\s+P:\/Projects\/LarchSys\/Skills  http:\/\/localhost:5290\/mcp  no headersHelper/)
    assert.match(line, /in that directory run: claude mcp remove workstate -s local$/)
})

test('a local entry WITH headersHelper is still reported: it shadows the user entry and its token', () => {
    const line = lineFor(finder([CONFIG]).lines, 'P:/Projects/Other')
    assert.ok(line, 'the Other local entry is reported')
    assert.match(line, /has headersHelper/)
    assert.match(line, /claude mcp remove workstate -s local$/)
})

test('a project .mcp.json entry is reported as the committed team file, not as a file to delete', () => {
    const line = finder([path.join(FIXTURES, 'none.json'), '--dir', REPO]).lines.find((l) => l.startsWith('project'))
    assert.ok(line, 'the .mcp.json entry is reported')
    assert.ok(line.includes(path.join(REPO, '.mcp.json')))
    assert.match(line, /mcpServers\.workstate in the committed team file/)
    assert.match(line, /do not delete the file/)
    assert.match(line, /claude mcp remove workstate -s project/)
})

test('another server name is ignored, and --name selects it instead', () => {
    const { lines } = finder([CONFIG])
    assert.equal(lines.length, 2)
    assert.ok(!lines.some((l) => l.includes('Unrelated')))
    const staging = finder([CONFIG, '--name', 'workstate-staging']).lines
    assert.equal(staging.length, 1)
    assert.match(staging[0], /^local\s+P:\/Projects\/Unrelated .*claude mcp remove workstate-staging -s local$/)
})

test('--own skips the local entry the script itself just wrote', () => {
    const { lines } = finder([CONFIG, '--own', 'P:\\Projects\\LarchSys\\Skills\\'])
    assert.equal(lines.length, 1)
    assert.ok(lines[0].includes('P:/Projects/Other'))
})

test('no shadowing entry prints nothing', () => {
    const r = finder([path.join(FIXTURES, 'none.json'), '--dir', FIXTURES])
    assert.equal(r.stdout, '')
    assert.equal(r.stderr, '')
})

test('a malformed or missing config prints one line and exits 0', () => {
    const bad = finder([path.join(FIXTURES, 'malformed.json')])
    assert.deepEqual(bad.lines, [`not checked: ${path.join(FIXTURES, 'malformed.json')} is not valid JSON`])
    const missing = finder([path.join(FIXTURES, 'does-not-exist.json')])
    assert.deepEqual(missing.lines, [`not checked: ${path.join(FIXTURES, 'does-not-exist.json')} is missing`])
})

test('no header value or token ever reaches stdout or stderr', () => {
    for (const args of [[CONFIG, '--dir', REPO], [CONFIG, '--name', 'workstate-staging'], [path.join(FIXTURES, 'malformed.json'), '--dir', REPO]]) {
        const r = finder(args)
        assert.ok(r.lines.length > 0, `${args.join(' ')} printed something to check`)
        for (const stream of [r.stdout, r.stderr]) {
            assert.ok(!stream.includes('SENTINEL-TOKEN'), `a sentinel leaked for ${args.join(' ')}:\n${stream}`)
            assert.ok(!stream.includes('Bearer'), `a header leaked for ${args.join(' ')}:\n${stream}`)
        }
    }
})
