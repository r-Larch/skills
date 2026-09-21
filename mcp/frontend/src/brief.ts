/**
 * The centre pane's only transform.
 *
 * Rule 1 of the unit: the brief is the agent's artifact, byte for byte. So this splits the payload
 * into lines and attaches a tone to each one — it never edits, re-flows, re-orders or tokenises a
 * line's text. `rejoin(briefLines(text)) === text` for every input, and `verify/brief-identity.mjs`
 * asserts exactly that against the live endpoint. Any future rule that cannot keep that identity
 * true is a rule that does not belong here.
 */

export type BriefTone = '' | 'attention' | 'refused'

export type BriefLine = { text: string; tone: BriefTone }

/**
 * doc 06 §5's attention block and nothing else.
 *
 * Deliberately narrower than the prototype, which also paints the `!` of a BINDING line red. A
 * binding constraint is permanent and asks for no decision, and doc 09 §3 spends colour only where
 * a decision is needed — so a brief with nothing wrong renders entirely grey, which is §3's own
 * test of whether the rule was followed.
 */
const ATTENTION_HEADER = /^-{2,}\s*attention\b/
const ATTENTION_LINE = /^\s*⚠/
const REFUSAL = /^REFUSED\b/

function tone(line: string): BriefTone {
  if (REFUSAL.test(line)) return 'refused'
  if (ATTENTION_HEADER.test(line) || ATTENTION_LINE.test(line)) return 'attention'
  return ''
}

export function briefLines(brief: string): BriefLine[] {
  return brief.split('\n').map((text) => ({ text, tone: tone(text) }))
}

/** The inverse, so the identity is something a script can execute rather than something claimed. */
export function rejoin(lines: BriefLine[]): string {
  return lines.map((line) => line.text).join('\n')
}
