---
description: Read open orchestrate-retro issues, triage the proposals, apply the good ones, and open a PR.
---

# Apply retro proposals to the orchestrate skill

You are running the **consumer half** of the orchestrate self-improvement loop. At the end of a long
run the `orchestrate` skill files a GitHub issue against itself, labelled `orchestrate-retro`,
proposing one concrete edit to one of its own files. Your job: read the open ones, decide which
deserve to land, apply them, verify, open a PR, and close what you resolved.

**Read `plugins/rlarch/skills/orchestrate/references/retro.md` before you touch anything.** It is the
contract — the label, the title format, and the exact body schema you are about to parse are defined
there and nowhere else. Do not work from memory of the schema; read the file. Everything below assumes
you have.

The skill proposing these edits wrote them about itself, at the end of a run, with a nearly-full
context. That is a real signal, but it is a weak one. **Step 2 is the point of this command.** A loop
that applies whatever an LLM proposed about itself is a machine for slowly degrading the skill.

## 1. Fetch the open proposals

```bash
gh issue list \
  --repo r-Larch/skills \
  --label orchestrate-retro \
  --state open \
  --limit 50 \
  --json number,title,body,comments,url,createdAt
```

If this returns `[]`, say **"No open retro proposals."** and stop. That is the normal, healthy case —
the retro is designed to fire rarely, and an empty list means recent runs produced no evidence the
skill malfunctioned. Do not go looking for closed issues to reopen.

For anything you need to read in full, including the recurrence comments:

```bash
gh issue view <number> --repo r-Larch/skills --comments
```

Parse each body into: **Target** (file › section), **Symptom**, **Root cause**, **Proposed edit**
(mode + `BEFORE:` / `AFTER:` blocks), **Token delta**, **Evidence**, **Run**. If an issue is missing
`## Target` or `## Proposed edit`, or its `BEFORE:`/`AFTER:` blocks are absent for a mode that requires
them, it is malformed — do not guess the intent. Close it as rejected per 2.4 with the reason
"malformed — does not match the schema in `references/retro.md`".

## 2. Triage — decide what actually gets applied

Work through these in order. Each is a rule with a threshold, not a mood.

### 2.1 Recurrence is the strong signal — apply it first

The retro is told to comment on an existing issue rather than open a near-duplicate, so recurrence
usually shows as **`### Recurrence —` comments on one issue**, not as several issues. Count both:

- comments matching `### Recurrence` on a single issue, **and**
- separate open issues whose `## Target` names the **same file and the same section** — those are a
  duplicate the producer failed to match on, and they count as recurrences of one finding.

**Two open issues on one section get one edit, not two.** They are two symptoms of one gap, and
applying both bolts two overlapping bullets onto a list that had none. Find the abstraction that
covers both — often a rule the skill already states at a different stage, in another file — write the
single sentence that carries it, and count it once against the budget. If no one rule covers both,
they were never siblings: rank them separately.

Rank by total recurrence count, descending. **Two or more recurrences → apply this one first.** The
run produced the same failure more than once; that is the only evidence in this whole system that
isn't a single model's opinion of its own instructions.

**At three or more recurrences, do not paste the proposed edit.** `retro.md` is explicit that a third
recurrence means the proposed edit is **under-specified, not unimportant** — the fix has already been
tried in spirit twice and the failure kept happening. Treat the issue as a bug report, not a patch:

- Read the target section in full, plus the recurrence comments (each says how it manifested that run).
- Work out why the proposed wording would not have prevented recurrences 2 and 3.
- Write a **stronger** edit yourself — usually more specific (a named test, a threshold, a concrete
  command) rather than longer.
- Say so in the PR body: *"reworked, not applied as proposed — 3rd recurrence."*

Applying an under-specified edit verbatim is how you get a fourth recurrence and a fatter skill.

### 2.2 A single occurrence is weak evidence

One issue, no recurrence comments, means one run went badly and the skill's own retro blamed one of its
sentences. That is a hypothesis. **Apply it only if it stands on its own merit** — read the target
section and ask:

- Is the quoted root-cause sentence *actually* wrong, ambiguous, or missing, reading it cold today?
- Would the AFTER text have prevented the symptom, or does it just describe the symptom?
- Is it a correction of a **specific sentence**, or general wisdom in the shape of an edit?

If you cannot answer the first two yes on the merits, **leave it open**. It is not rejected — a second
run may recur on it, and then 2.1 decides. Being filed is not evidence.

### 2.3 Enforce the token budget — aggregate, not per-issue

Every issue carries `## Token delta`. `retro.md` caps a single proposal at `+60`; nothing caps a batch,
so this command does:

> **The aggregate net token delta of one `/apply-retro` PR must be `≤ +40`.**
> Sum the signed deltas of every proposal you intend to apply. Prefer a negative total.

Sum them before you edit anything. If the batch total exceeds `+40`:

1. Drop the weakest **additive** proposals (mode `add`) first, lowest recurrence count first, until the
   total is within budget. Dropped ≠ rejected — leave those issues open.
2. Where a dropped proposal is genuinely worth having, **convert it to a replacement**: find the weak
   sentence in the same section that the new text supersedes, and replace it instead of appending.
   Recompute the delta and say in the PR body that you converted it.
3. Never balance the budget by trimming unrelated prose in the same file to "make room". That is an
   uninspected edit riding along on a reviewed one.

Three or more `add`-mode proposals in one batch is itself a warning sign: the skill is being asked to
grow every week, and it is a token-saving skill. When in doubt, ship fewer.

### 2.4 Advice gets closed as rejected, with a reason

A proposal that is advice rather than a correction of a specific sentence does **not** get quietly
left open — silence lets the retro re-file it forever. Close it, with the reason stated. Reject when:

- the AFTER text is a new paragraph of general guidance not anchored to any sentence in the target;
- the target section is named but the root cause is "no rule covers this" **and** the symptom is a
  one-off that no rule could reasonably have caught;
- the fix restates something the skill already says elsewhere;
- the finding is about the undertaking, not the skill (`retro.md` › *the subject of a retro is this
  skill, never the undertaking*).

```bash
gh issue close <number> \
  --repo r-Larch/skills \
  --reason "not planned" \
  --comment "Rejected: <one or two sentences — which rejection test it failed and why>. Reopen with a recurrence if this costs a run again."
```

Give a real reason. The retro's duplicate search covers closed issues too and treats a
`CLOSED`/`NOT_PLANNED` match on the same `## Target` as **do not re-file** — so a closed issue with a
stated reason is the only way that judgment gets back into the loop. Close it without one and the
retro sees a rejection it cannot read.

At the end of step 2 you should have four lists: **apply**, **rework-and-apply**, **leave open**,
**reject**. Show them to the user before you start editing.

## 3. Apply the edits

Target files live under `plugins/rlarch/skills/orchestrate/` — `SKILL.md`,
`references/{briefs,decompose,retro}.md`, `assets/{CONVENTIONS,LEDGER}.template.md`. The issue's
`## Target` line gives the path relative to that skill root.

**The `BEFORE:` block must match the target file character for character.** Locate it with an exact
search, never a fuzzy one:

```bash
git grep -n -F "<first line of the BEFORE block>" -- plugins/rlarch/skills/orchestrate/
```

Then apply with `Edit` using the full `BEFORE:` text as `old_string` and the full `AFTER:` text as
`new_string`. `Edit` fails loudly on a non-unique or non-matching `old_string` — that failure is
information, not an obstacle to route around.

**If `BEFORE:` does not match exactly, the issue is stale.** The skill was edited after the issue was
filed, so the retro's reading of the file is out of date and its proposed AFTER may no longer make
sense in context. **Never fuzzy-apply, never hand-reconcile the whitespace, never "apply the spirit
of it".** A fuzzy apply silently corrupts a file that has no test suite to catch it. Instead:

- **Ask the user**, showing both the issue's `BEFORE:` and the current file text, or
- comment on the issue that it is stale and leave it open for the retro to re-file against current
  text:

```bash
gh issue comment <number> \
  --repo r-Larch/skills \
  --body "Stale: the BEFORE block no longer matches \`<file>\` (edited since this was filed). Not applied. Re-file against current text if this still recurs."
```

For mode `add`, `BEFORE:` is `n/a` — but the AFTER text still has to land somewhere specific. Anchor it
to the nearest sentence the issue's root cause quotes. If the issue gives you no anchor, treat it as
malformed (2.4), not as licence to place it where you like.

### Align the siblings — one issue, one *rule*, however many files state it

The skill states the same rule in several places on purpose: `SKILL.md` tells the orchestrator,
`references/briefs.md` tells the worker, `references/decompose.md` tells the planner. Correcting one
copy and leaving its twin standing does not fix the skill — it makes the skill disagree with itself,
and the copy you left behind is often the one that gets read at the moment it matters.

After each edit lands, search the other files for the rule you just changed:

```bash
git grep -n -i "<a load-bearing phrase from the text you changed>" -- plugins/rlarch/skills/orchestrate/
```

Fix only these three things:

- **a restatement of the sentence you corrected** — bring it into line; it is the same fix, not a new one;
- **a dangling reference your edit created** — a marker, term, or report field one file now tells an
  agent to write and no file tells anyone to read;
- **a sentence that now contradicts the edit** — change it, or revert your edit rather than ship both.

The test is *does this text state the rule I just changed?*, not *could this be better?* Anything else
you notice is out of scope: leave it, or let the retro file it.

An alignment edit rides on the issue that motivated it — same commit, same PR-table row, and its delta
counts toward the batch budget (2.3). It is not an unattributed edit; it is the rest of the edit you
already justified.

Beyond that: one issue = one edit. Do not batch unrelated tidy-ups into this branch.

## 4. Verify

```bash
claude plugin validate plugins/rlarch --strict
```

`--strict` turns warnings into a non-zero exit. Read the actual result line; it must be green before
you open a PR.

**Then read back every section you edited.** This is not optional and it is not a formality. There is
no test suite for prose — validation checks manifest and structure, and would happily pass a skill
whose guidance you just made incoherent. The read-back is the only real check that exists. For each
edited file, read the surrounding section and confirm:

- the edit landed in the section `## Target` named, not a similar-looking one elsewhere;
- the new sentence reads coherently with the sentences on either side of it — no dangling "the above",
  no duplicated rule, no list item that no longer parallels its siblings;
- nothing else in the **skill** now contradicts it — you ran the sibling search above and the twins agree;
- the actual token change is roughly what the issue claimed. If a `+20` proposal landed as `+90`,
  you rewrote it — go back to 2.3 and re-check the aggregate.

If a read-back reads badly, fix the wording now or revert that one edit and leave its issue open. Do
not ship prose you had to squint at.

## 5. Open the PR

Branch from `master`, not from whatever you are on:

```bash
git switch -c retro/apply-<YYYY-MM-DD> master
git add plugins/rlarch/skills/orchestrate/
git commit -m "orchestrate: apply retro proposals #<n>, #<n>"
git push -u origin HEAD
```

Write the PR body to a file first — multi-line Markdown through `--body` is a quoting hazard on
PowerShell. `.claude/orchestration/` is gitignored, so a body file there can never ride along in the
commit.

```bash
gh pr create \
  --repo r-Larch/skills \
  --base master \
  --head "$(git branch --show-current)" \
  --title "orchestrate: apply retro proposals (<n> issues, net <±N> tokens)" \
  --body-file .claude/orchestration/apply-retro-pr-body.md
```

The body must list, one row per applied proposal: **issue number**, **target file › section**, **mode**,
**token delta**, and whether it was applied as proposed, reworked, or merged with a sibling issue.
List an alignment edit as its own row under the issue it rides on. Then the **net delta for the
batch**, and one line each for anything left open or rejected, so the next run of this command starts
from a written record rather than re-deriving your triage.

```markdown
| Issue | Target | Mode | Δ tokens | Applied |
|---|---|---|---|---|
| #12 | `references/briefs.md` › Recon agents | add | +27 | as proposed |
| #9  | `SKILL.md` › The risk dial | tighten | -14 | reworked — 3rd recurrence |

**Net: +13 tokens** (budget +40)

Left open: #15 (single occurrence, unconvincing on merit).
Rejected: #11 (advice, not a sentence-level correction) — closed with reason.
```

Add `--dry-run` to print the PR instead of creating it if you want to check the body first.

## 6. Close the loop

Each **applied** issue gets closed with a comment linking the PR:

```bash
gh issue close <number> \
  --repo r-Larch/skills \
  --reason completed \
  --comment "Applied in <PR url>."
```

Where you reworked rather than pasted, say so and say why in that comment — that is the record the next
retro reads when it considers re-filing.

**Rejected** issues were already closed in 2.4 with their reason. **Left-open** issues stay open,
untouched; do not comment "deferred" on them — an empty comment thread is what lets 2.1 count real
recurrences later.

## Conventions

- PRs target `master`. Never commit straight to it.
- Only `plugins/rlarch/skills/orchestrate/**` is in scope for the edits. If a proposal targets anything
  else, it is off-contract — reject it (2.4).
- Every proposal that lands must trace to an issue number in the PR body. No unattributed edits — an
  alignment edit traces to the issue it rides on.
- Verification is `claude plugin validate plugins/rlarch --strict` **plus** the step 4 read-back. Green
  validation alone is not verification of prose.
- When the batch is borderline, ship fewer proposals. A skill that grows a little every week stops
  being worth loading, and that failure is invisible until it is expensive.
- Leave a short summary: how many issues were open, what you applied, the net token delta, what you
  rejected and why, and what you left for next time.
