// @ts-check
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { resolveClaude, parseBackgroundId } from '../claude.mjs'
import { parseEnvFile } from '../workstate.mjs'
import { tmp } from './helpers.mjs'

test('backgrounded line: id read through ANSI colour codes', () => {
    assert.equal(parseBackgroundId('\x1b[2mbackgrounded\x1b[0m · \x1b[1;36mc88dd0c8\x1b[0m · fix-B-07\n'), 'c88dd0c8')
    assert.equal(parseBackgroundId('backgrounded | c88dd0c8 | x'), 'c88dd0c8')
    assert.equal(parseBackgroundId('error: nope'), undefined)
})

test('env file: comments and blanks ignored, quotes stripped, export accepted', () => {
    assert.deepEqual(parseEnvFile('﻿# c\n\nA=1\nB="two words"\r\nexport C=\'3\'\nD=\nbad line\n'), { A: '1', B: 'two words', C: '3', D: '' })
})

test('CLAUDE_BIN must be a JSON array', () => {
    assert.deepEqual(resolveClaude({ CLAUDE_BIN: '["node","x.mjs"]' }), ['node', 'x.mjs'])
    assert.throws(() => resolveClaude({ CLAUDE_BIN: 'node x.mjs' }), /JSON array/)
})

test('Windows: claude.exe preferred; a lone claude.cmd is refused', { skip: process.platform !== 'win32' }, () => {
    const cmdOnly = tmp('cmd')
    fs.writeFileSync(path.join(cmdOnly, 'claude.cmd'), '@echo off\n')
    const withExe = tmp('exe')
    fs.writeFileSync(path.join(withExe, 'claude.exe'), '')
    const emptyHome = tmp('home')
    assert.deepEqual(resolveClaude({ PATH: [cmdOnly, withExe].join(path.delimiter), USERPROFILE: emptyHome }), [path.join(withExe, 'claude.exe')])
    assert.throws(() => resolveClaude({ PATH: cmdOnly, USERPROFILE: emptyHome }), /only a claude\.cmd shim/)
    fs.mkdirSync(path.join(emptyHome, '.local', 'bin'), { recursive: true })
    fs.writeFileSync(path.join(emptyHome, '.local', 'bin', 'claude.exe'), '')
    assert.deepEqual(resolveClaude({ PATH: cmdOnly, USERPROFILE: emptyHome }), [path.join(emptyHome, '.local', 'bin', 'claude.exe')])
})
