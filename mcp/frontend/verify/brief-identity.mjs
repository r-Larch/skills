/**
 * Rule 1 of U7, made executable: the centre pane is the agent's artifact, byte for byte.
 *
 * There is no browser here, so pixels cannot be compared — strings can. This signs in, fetches
 * `/api/runs/{ref}/brief`, runs the *same* transform the component applies (`src/brief.ts`, imported,
 * not reimplemented) and asserts the text that comes out is identical to the text that went in,
 * codepoint for codepoint. If the renderer ever does something a `<pre>` would not, this fails.
 *
 *   node verify/brief-identity.mjs [baseUrl] [ref …]
 *
 * The credential is read by `verify/session.mjs`, never written here.
 */
import { briefLines, rejoin } from '../src/brief.ts'
import { signIn } from './session.mjs'

const base = process.argv[2] ?? 'http://localhost:5290'
const refs = process.argv.length > 3 ? process.argv.slice(3) : ['entity-store', 'content-store']

/**
 * The live corpus happens to contain no trailing whitespace, no tabs and no CR, so a transform that
 * trimmed line ends would pass against it and still be wrong. These strings are the cases the corpus
 * does not supply.
 */
const ADVERSARIAL = {
  'trailing spaces': 'RUN x   \n  ! never   \n',
  'tabs and CRLF': 'A\tB\r\nC\r\n',
  'no final newline': 'last line has no newline',
  'blank lines only': '\n\n\n',
  'lone attention glyph': '--- attention (1) ---\n⚠ 2.8 claim expired — work_status(ref:"2.8")',
  empty: '',
}

let failures = 0

for (const [name, text] of Object.entries(ADVERSARIAL)) {
  if (rejoin(briefLines(text)) === text) {
    console.log(`PASS synthetic "${name}": ${JSON.stringify(text).length} chars round-trip exactly`)
  } else {
    console.log(`FAIL synthetic "${name}": ${JSON.stringify(rejoin(briefLines(text)))} !== ${JSON.stringify(text)}`)
    failures += 1
  }
}

const cookie = await signIn(base)

for (const ref of refs) {
  const response = await fetch(`${base}/api/runs/${encodeURIComponent(ref)}/brief`, {
    headers: { cookie },
  })

  if (!response.ok) {
    console.log(`FAIL ${ref}: GET brief → ${response.status}`)
    failures += 1
    continue
  }

  const contentType = response.headers.get('content-type') ?? ''
  const original = await response.text()
  const rendered = rejoin(briefLines(original))

  const identical = rendered === original
  const lines = briefLines(original)
  const toned = lines.filter((l) => l.tone !== '').length

  if (!identical) {
    const at = [...original].findIndex((ch, i) => ch !== rendered[i])
    console.log(`FAIL ${ref}: transform is not the identity — first difference at codepoint ${at}`)
    failures += 1
    continue
  }

  console.log(
    `PASS ${ref}: ${original.length} chars, ${lines.length} lines, ${toned} toned, ` +
      `content-type "${contentType}" — rejoin(briefLines(x)) === x`,
  )
}

if (failures > 0) {
  console.log(`\n${failures} identity check(s) failed.`)
  process.exitCode = 1
} else {
  console.log(
    `\nAll ${Object.keys(ADVERSARIAL).length} synthetic cases and ${refs.length} live briefs render byte-identically.`,
  )
}
