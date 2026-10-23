// fake-claude.mjs — stands in for the claude CLI via CLAUDE_BIN. Records every argv to
// FAKE_CLAUDE_LOG (one JSON line per call) and answers --bg, agents --json, stop and rm.
// @ts-check

import fs from 'node:fs'

const args = process.argv.slice(2)
if (process.env.FAKE_CLAUDE_LOG) {
    fs.appendFileSync(process.env.FAKE_CLAUDE_LOG, JSON.stringify({ argv: args, cwd: process.cwd() }) + '\n')
}

if (args[0] === 'agents') {
    process.stdout.write(process.env.FAKE_CLAUDE_AGENTS ?? JSON.stringify([
        { pid: 4242, id: 'abc12345', name: 'probe', status: 'busy', state: 'running', cwd: process.cwd(), sessionId: '11111111-2222-3333-4444-555555555555' },
        { pid: 4343, id: 'ffff0000', name: 'other', status: 'waiting', waitingFor: 'permission prompt', cwd: process.cwd() },
    ]))
    process.exit(0)
}
if (args[0] === 'stop' || args[0] === 'rm') {
    process.stdout.write(`${args[0]} ${args[1]}\n`)
    process.exit(0)
}
if (args.includes('--bg')) {
    if (process.env.FAKE_CLAUDE_BG_FAIL) {
        process.stderr.write('error: fake launch failure\n')
        process.exit(1)
    }
    const name = args[args.indexOf('--name') + 1]
    process.stdout.write(`\x1b[2mbackgrounded\x1b[0m · \x1b[1;36mabc12345\x1b[0m · ${name}\n`)
    process.exit(0)
}
process.stderr.write(`fake-claude: unexpected argv ${JSON.stringify(args)}\n`)
process.exit(2)
