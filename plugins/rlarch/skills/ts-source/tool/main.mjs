// main.mjs — subcommand dispatch. One process, git-style verbs.
// @ts-check

import { Args } from './args.mjs'
import { UserError, out, note } from './common.mjs'
import { resolveProject, resolveIdOnly } from './discovery.mjs'
import * as Tier1 from './tier1.mjs'
import * as Tier2 from './tier2.mjs'
import * as Dead from './dead.mjs'
import * as Discover from './discover.mjs'
import * as Server from './server.mjs'

export const VERSION = '0.1.0'

/** Commands that need a Program: worth routing to a warm daemon if one is serving this project. */
const SEMANTIC = new Set(['find-usages', 'impls', 'calls', 'dead'])

/**
 * @param {string[]} argv
 * @param {{ memo?: Map<string, any>, services?: Map<string, any>, project?: any, inDaemon?: boolean }} [ctx]
 */
export async function run(argv, ctx = {}) {
    if (argv.length === 0 || argv[0] === '-h' || argv[0] === '--help' || argv[0] === 'help') {
        usage()
        return 1
    }

    // `tss --root . discover` is a natural thing to type and used to die with "unknown command:
    // --root". The verb can appear anywhere; hoist the first non-flag token to the front.
    argv = hoistCommand(argv)

    const cmd = argv[0].toLowerCase()
    const a = new Args(argv.slice(1))
    const t0 = Date.now()

    try {
        if (cmd === 'version' || cmd === '--version') {
            out(VERSION)
            return 0
        }
        if (cmd === 'serve') return await Server.serve(a, t0)
        if (cmd === 'status') return await Server.status(a, t0)

        // Route to a warm daemon when one happens to be up. The probe is identity-only (no config
        // parse, no compiler load) and failing is the normal case, not an error.
        if (!ctx.inDaemon && SEMANTIC.has(cmd)) {
            const id = resolveIdOnly(a)
            const resp = await Server.tryAsk(id, { cmd, argv: argv.slice(1) })
            if (resp) {
                process.stdout.write(resp.output)
                return resp.exit
            }
        }

        const p = ctx.project ?? resolveProject(a)

        switch (cmd) {
            case 'discover': return Discover.discover(p, a, t0, ctx)
            case 'search': return Tier1.search(p, a, t0, ctx)
            case 'outline': return Tier1.outline(p, a, t0, ctx)
            case 'tree': return Tier1.tree(p, a, t0, ctx)
            case 'metrics': return Tier1.metrics(p, a, t0, ctx)
            case 'graph': return Tier1.graph(p, a, t0, ctx)
            case 'dead': return Dead.dead(p, a, t0, ctx)
            case 'find-usages': return Tier2.findUsages(p, a, t0, ctx)
            case 'impls': return Tier2.impls(p, a, t0, ctx)
            case 'calls': return Tier2.calls(p, a, t0, ctx)
            default:
                note(`unknown command: ${cmd}`)
                usage()
                return 1
        }
    } catch (e) {
        if (e instanceof UserError) {
            note(`error: ${e.message}`)
            return 2
        }
        throw e
    }
}

const COMMANDS = new Set([
    'version', '--version', 'serve', 'status', 'discover', 'search', 'outline', 'tree',
    'metrics', 'graph', 'dead', 'find-usages', 'impls', 'calls',
])

/** Move the verb to argv[0] when the user put flags first. Leaves a known verb untouched. */
function hoistCommand(argv) {
    if (COMMANDS.has(argv[0].toLowerCase())) return argv
    const at = argv.findIndex((t) => COMMANDS.has(t.toLowerCase()))
    if (at <= 0) return argv
    return [argv[at], ...argv.slice(0, at), ...argv.slice(at + 1)]
}

function usage() {
    note(`ts-source ${VERSION} — source-level navigation for the TS/JS project you're editing.

Tier 1 — syntax only. No build, no typecheck. Works on broken code, sees non-exported declarations.
  search <pattern>          [--kind component,hook,fn,class,interface,type,enum,const,method,prop]
                            [--regex] [--exported] [--top N] [--json]
  outline <Name>            a declaration and its members  |  outline --file <path>
  tree [dirFilter]          directory -> files / declarations / exports / lines
  metrics [--sort loc|decls|exports|imports|components] [--of type|file] [--top N]
  graph <file>              [--importers|--imports]   who pulls this module in, and what it pulls

Tier 1.5 — the module graph. Structural, exact, no program build.
  dead [--files] [--exports] [--locals]  [--entry <glob,…>] [--include-public] [--include-tests]
                            default: --files --exports. --locals adds a checker pass (slower).

Tier 2 — semantic. Needs a resolvable Program (node_modules installed); no build.
  find-usages <symbol>      every reference, JSX usage included
  impls <Interface|Class>   who implements / extends it        [--derived]
  calls <fn> [--callers|--callees]

Keep-alive (optional, helps Tier 2 and --locals)
  serve [--project <tsconfig>]   start a warm daemon and return   |  serve --stop
  status                         is a daemon serving this project?

Utility
  discover [--semantic]     what the tool actually sees. START HERE if a count looks wrong.
  version

Target selection (every command):
  --project <tsconfig.json>   the project to work on. Prefer this; it identifies the daemon too.
  --root <dir>                no tsconfig: scan this directory tree
  --ts <path>                 use a specific TypeScript compiler (default: the project's own)
Default: walk up from cwd for tsconfig.json, else treat cwd as the root.`)
}
