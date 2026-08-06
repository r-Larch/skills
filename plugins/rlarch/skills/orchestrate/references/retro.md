# Retro — how this skill improves itself

**This file is the contract.** It defines when the orchestrator files a retrospective issue, what that issue must contain, and the exact commands to file it. The repo-local command that later reads those issues and opens a PR is told to read the schema *from here* rather than restate it. If the schema changes, it changes in this file and nowhere else.

The subject of a retro is **this skill**, never the undertaking. "The migration was harder than planned" is not a finding. "`references/briefs.md` let a worker ship an unverified claim" is.

---

## The retro must be cheap

It runs at the very end of a long run, against a context that is nearly full. It is built **only** from what you already hold:

- `LEDGER.md` — the verdicts, exceptions, assumptions and follow-ups you wrote as you went;
- the worker and reviewer reports you already read;
- the verification output you already ran.

**Do not** re-read source files, re-run recon, re-run verification, spawn a review agent to write or check the retro, or re-read worker output you summarized. A retro that costs a review agent has already destroyed more than it can return — this is a token-saving skill.

*One read is permitted, and only one:* the single skill file you are proposing to edit, so you can quote its current text verbatim. Skip even that when the file is already in context. If quoting it would cost a read you can't afford, file nothing — a vague proposal the consumer can't apply is worse than silence.

Target cost for the whole step: **under ~2k tokens**. If it is costing more than that, you are researching, not retrospecting.

---

## Noise floor — skipping is the default

**Run the retro only if at least one of these is true, read straight off the ledger:**

| Trigger | Where it shows |
|---|---|
| ≥1 `REWORKED` verdict | task log |
| ≥1 `RE-PLANNED` verdict | task log |
| ≥1 verification came back red and needed chasing | task log `Verify` column |
| A phase gate returned a blocker | gate entry |
| A single traceable waste of **≥10k tokens** caused by this skill's guidance | exceptions / your own recollection of the run |

Otherwise **skip entirely**. No issue, no comment, no prose — one ledger line: `Retro: skipped — no reworks, no re-plans, no failed verifications.` Then stop.

A run with no reworks, no re-plans and no failed verifications produced no evidence that the skill malfunctioned. Anything you wrote about it would be speculation, and speculation is exactly what bloats a skill.

The 10k floor is set at roughly one recon agent (see the price table in `SKILL.md`). Below that, the owner's time reading the issue costs more than the waste it describes.

**At most one issue per run.** Pick the single highest-value finding and drop the rest; two findings that share a root cause are one finding. This cap is the main defence against the failure mode described below — it is not a soft guideline.

---

## Why this is designed to fire rarely

The way this mechanism fails is not that it breaks. It is that it files a plausible-sounding issue after every run, the owner stops reading them, and the skill slowly accumulates advice until it costs more than it saves — a self-improvement loop that destroys the thing it protects.

Three rules exist against that, and they are load-bearing:

1. the noise floor above, with **skip as the default**;
2. one issue per run, maximum;
3. every proposed edit carries a **token delta**, and additive edits are the suspect case.

If you find yourself reaching for a trigger to justify an issue you already want to file, that is the failure mode, in progress. File nothing.

---

## What counts as a finding

A finding is a **concrete edit to a named section of this skill**. It must state all four:

1. **Symptom** — what actually went wrong this run, in outcome terms.
2. **Target** — the file *and* section heading in the skill that permitted it.
3. **Proposed edit** — the literal before/after text.
4. **Token delta** — signed, and honest.

If you cannot name the section, you do not have a finding — you have an observation about the undertaking. Drop it.

| Not a finding | A finding |
|---|---|
| "Recon was wasteful this run." | "`briefs.md` › *Recon agents* should require an asserted capability to be checked against `--help` before it reaches a user-facing doc." |
| "The plan was too coarse." | "`decompose.md` › *Watch for the mis-sized task* names the contract-question failure but gives no test; add the two-word test `is the real content 'decide the rule'?`" |
| "Reviews cost a lot." | "`SKILL.md` › *The risk dial* lists six review triggers; the equivalence-claim one fired on a doc-only task. Narrow it to code." |

### Token delta, and the bias against adding

State the delta on every proposal. **Prefer replacing or tightening existing text over appending new text.**

- `-N` or `±0` — the good case. The skill already almost said it; the sentence was too weak, in the wrong file, or in a section the orchestrator doesn't read at that step.
- `+1..+60` — acceptable if you can say why the same effect cannot be had by tightening what is already there.
- `+60` or more — **do not file it.** A fix you cannot express in sixty tokens is a fix you do not yet understand.

**A finding whose fix is "add another paragraph of advice" is usually a bad finding.** The skill is dense on purpose. If your proposal reads like general wisdom rather than a correction of a specific sentence, it is advice, not a fix — drop it.

Mark the proposal's mode explicitly: `replace`, `tighten`, `move`, or `add`. `add` is the one that needs an argument.

---

## Safety — the issue is public and automatic

**It posts without asking.** The owner chose no confirmation prompt; do not add one, and do not ask "shall I file this?" — either the triggers fired and you file, or they didn't and you skip.

Because it posts unattended into a world-readable repo, the body carries **findings and ledger excerpts only**:

- **Allowed:** ledger rows, verdicts, verify commands and their result lines, worker-report lines (`DEVIATIONS`, `RED-CHECK`), and verbatim text from this skill's own files.
- **Never:** source file contents, diffs, code from the repo being worked on, credentials, tokens, connection strings, absolute paths outside the repo, customer or client names, anything from a private codebase.

If a ledger row you want to quote contains any of that, redact the part and keep the shape (`… tenant `<redacted>` …`), or pick different evidence. When in doubt, cut the excerpt — the finding stands on the proposed edit, not on the quote.

---

## Procedure

### 0. Label bootstrap — once per repo

```bash
gh label create orchestrate-retro \
  --repo r-Larch/skills \
  --description "Self-proposed edit to the orchestrate skill, filed by its own retro" \
  --color 5319E7
```

This fails with *already exists* on every run after the first. That is the expected steady state — do not retry it, and do not add `--force`, which would overwrite the owner's colour and description.

### 1. Check the noise floor

Read the ledger's task log. If no trigger fired, write the skip line and stop. This is the common outcome.

### 2. Search for an existing issue with the same root cause

**Required before creating anything.**

```bash
gh issue list \
  --repo r-Larch/skills \
  --label orchestrate-retro \
  --state open \
  --limit 50 \
  --json number,title,body \
  --jq '.[] | "#\(.number)\t\(.title)"'
```

Match on **root cause, not wording** — same file and same section is a duplicate even if the symptom looked different. Pull the full body of a candidate with `--jq '.[] | select(.number==<n>) | .body'` on the same command rather than a second fetch.

If a match exists, **comment; do not open a near-duplicate**:

```bash
gh issue comment <number> \
  --repo r-Larch/skills \
  --body-file .claude/orchestration/<slug>/retro-comment.md
```

Comment body, exactly:

```markdown
### Recurrence — <run-slug>, <YYYY-MM-DD>
<One line: how it manifested this run.>
Evidence: <one ledger row or verify line>
Proposal: unchanged | revised — <what changed and the new token delta>
```

If the issue already carries two recurrence comments, add yours and note in the ledger that this finding has now recurred three times. A third recurrence is the owner's signal that the proposed edit is under-specified, not that it is unimportant.

### 3. Otherwise, create the issue

Write the body to a file first — multi-line Markdown through `--body` is a quoting hazard on PowerShell.

```bash
gh issue create \
  --repo r-Larch/skills \
  --title "retro: <file>#<section-slug> — <symptom in ≤8 words>" \
  --label orchestrate-retro \
  --body-file .claude/orchestration/<slug>/retro-issue.md
```

### 4. Record it

One ledger line with the issue number or URL, and one line in your closing report to the user. Do not summarize the issue back to them — they can open it.

---

## The issue schema

Both halves of this mechanism depend on this being exact: the retro **writes** it, the repo command **parses** it.

### Label

`orchestrate-retro` — every issue, no exceptions. It is the only thing the consumer filters on.

### Title

```
retro: <file>#<section-slug> — <symptom in ≤8 words>
```

`<file>` is the skill file relative to the skill root: `SKILL.md`, `references/briefs.md`, `references/decompose.md`, `references/retro.md`, `assets/LEDGER.template.md`. `<section-slug>` is the target heading, lowercased and hyphenated.

### Body — these headings, in this order, all required

| Heading | Contents |
|---|---|
| `## Target` | Skill-root-relative path in backticks, then `›`, then the target section heading **verbatim**. One line. |
| `## Symptom` | What went wrong this run, in outcome terms. 1–3 sentences. What it cost, if you can name a number. |
| `## Root cause` | The sentence or omission in the target section that permitted it. Quote the offending text if it exists; say "no rule covers this" if it doesn't. |
| `## Proposed edit` | `Mode:` one of `replace` / `tighten` / `move` / `add`, then a fenced `text` block labelled `BEFORE:` (verbatim from the file, or `n/a` for mode `add`) and a second fenced `text` block labelled `AFTER:`. The consumer applies these mechanically — BEFORE must match the file character for character. |
| `## Token delta` | `+N` / `-N` / `±0` tokens, then one line on why this is the cheapest form of the fix. Net-additive proposals must justify why tightening existing text cannot achieve it. |
| `## Evidence` | Ledger rows, verify lines, report lines. Nothing from the worked repo. 5 lines max. |
| `## Run` | One line: `Repo: <name> · Slug: <run-slug> · Date: <YYYY-MM-DD> · Reworks: <n> · Re-plans: <n> · Failed verifications: <n>` |

No other headings. No preamble above `## Target`. Nothing below `## Run`.

---

## Worked example

This is a real finding from the run that produced this file, filed in full.

**Title:** `retro: references/briefs.md#recon-agents — recon claim nearly shipped unverified`

**Body:** (outer fence is four backticks only so the inner blocks survive; the issue body itself starts at `## Target`)

````markdown
## Target
`references/briefs.md` › **Recon agents**

## Symptom
A recon agent asserted that a `skillDirectories` setting exists in Claude Code. It does
not — extracting strings from the installed binary showed only `extraKnownMarketplaces`,
`enabledPlugins`, `skillOverrides`. The claim was about to be written into a user-facing
upgrade guide. Disproving it cost roughly 15k tokens that the run had not budgeted.

## Root cause
The section tells the orchestrator how to *scope* recon — one question, ≤15 lines,
paths not content — but nothing in it distinguishes a coordinate a recon agent read off
disk from a capability it asserted from memory. Both come back in the same ≤15 lines and
read with identical confidence, so the answer was treated as evidence.

## Proposed edit
Mode: add

```text
BEFORE:
- Ask for **≤15 lines with file paths**. Coordinates, not content.
```

```text
AFTER:
- Ask for **≤15 lines with file paths**. Coordinates, not content.
- **A recon agent asserting that a capability, setting, or flag exists is not evidence.** Before it reaches a user-facing doc, confirm it against `--help` or the binary, or drop the claim.
```

## Token delta
+27 tokens. Additive, but tightening cannot achieve it: the section has no sentence about
claim provenance to strengthen, and the same 27 tokens would have saved the ~15k spent
disproving one hallucinated setting.

## Evidence
Ledger exception "Phase 0 — recon": the spec recon agent asserted `skillDirectories`;
binary string extraction disproved it before it reached `upgrade_guide.md`.
Standing rule then added to this run's CONVENTIONS.md — a per-run patch for a skill-level gap.

## Run
Repo: r-Larch/skills · Slug: one-plugin · Date: 2026-08-06 · Reworks: 0 · Re-plans: 1 · Failed verifications: 0
````

Note what makes it fileable: it names one section, quotes the exact line it changes, states a delta, and its evidence is two ledger rows — no source files, no diffs, no second read of anything.
