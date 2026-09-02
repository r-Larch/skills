// cli.mjs — the process entry point. Kept separate from main.mjs so the daemon can import `run`
// without also running the argv it was started with.
// @ts-check

import { run } from './main.mjs'
import { note } from './common.mjs'

const MIN_NODE = 18

const major = Number(process.versions.node.split('.')[0])
if (major < MIN_NODE) {
    note(`ts-source needs Node ${MIN_NODE}+ (found ${process.versions.node}).`)
    process.exit(2)
}

run(process.argv.slice(2))
    .then((code) => {
        process.exitCode = code
    })
    .catch((e) => {
        note(`ts-source: ${e?.stack ?? e}`)
        process.exitCode = 70
    })
