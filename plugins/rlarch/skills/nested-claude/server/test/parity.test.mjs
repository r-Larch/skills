// The three launch paths must write the same brief and grant the same tools. These parse the
// scripts rather than copying their text, so an edit to either side fails here.
// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { protocolHeader, READONLY_TOOLS, FIX_TOOLS } from '../header.mjs'

const assets = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'assets')
const ps1 = fs.readFileSync(path.join(assets, 'Start-NestedAgent.ps1'), 'utf8').replace(/\r\n/g, '\n')
const sh = fs.readFileSync(path.join(assets, 'start-nested-agent.sh'), 'utf8').replace(/\r\n/g, '\n')

test('protocol header equals the .ps1 here-string, LF, after substitution', () => {
    const m = /\$header = @"\n([\s\S]*?)\n"@/.exec(ps1)
    assert.ok(m, 'here-string not found in Start-NestedAgent.ps1')
    const controller = 'ctl-x [a1b2c3]'
    const name = 'unit-7'
    const expected = m[1].replaceAll('$Controller', controller).replaceAll('$Name', name)
    assert.equal(protocolHeader(controller, name), expected)
    assert.ok(!protocolHeader(controller, name).includes('\r'))
})

/**
 * Quoted entries of a PowerShell @( ... ) block, comment lines skipped.
 * @param {string} start
 */
function ps1Array(start) {
    const at = ps1.indexOf(start)
    assert.ok(at >= 0, `${start} not found`)
    const body = ps1.slice(at + start.length, ps1.indexOf('\n)', at))
    return body.split('\n').filter((l) => !l.trim().startsWith('#')).flatMap((l) => [...l.matchAll(/"([^"]*)"/g)].map((x) => x[1]))
}

/** @param {string} start */
function shArray(start) {
    const at = sh.indexOf(start)
    assert.ok(at >= 0, `${start} not found`)
    const body = sh.slice(at + start.length, sh.indexOf('\n)', at))
    return body.split('\n').filter((l) => !l.trim().startsWith('#'))
        .flatMap((l) => [...l.matchAll(/"([^"]*)"/g)].map((x) => x[1])).filter((x) => !x.startsWith('$'))
}

test('readonly and fix profiles equal the .ps1 arrays', () => {
    assert.deepEqual([...READONLY_TOOLS], ps1Array('$readonly = @('))
    assert.deepEqual([...FIX_TOOLS], [...ps1Array('$readonly = @('), ...ps1Array('$fix = $readonly + @(')])
})

test('readonly and fix profiles equal the .sh arrays', () => {
    assert.deepEqual([...READONLY_TOOLS], shArray('readonly_tools=('))
    assert.deepEqual([...FIX_TOOLS], [...shArray('readonly_tools=('), ...shArray('fix_tools=(')])
})

test('no profile grants git push', () => {
    assert.ok(!FIX_TOOLS.some((t) => /push/i.test(t)))
})
