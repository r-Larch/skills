# 09 — The operator dashboard: design plan

> **Not** built on the `nomos-service` dashboard blueprint — owner's call, and correct: that pattern
> is an operator console for a *request-processing service* (throughput, latency, a playground). This
> is a **supervision console for autonomous agents**, and almost nothing transfers.
>
> Visual direction is pinned by the brief: `FounderOS-DEMO`. Stack: **SolidJS + Tailwind**, as in
> `Nomos.Client/nomos`.

---

## 1. What this actually is, before any pixels

**Subject:** a watch floor for coding agents that never sleep and never volunteer that they are
stuck.

**Audience:** one operator. Not a team, not a stakeholder — the person who launched the agents and
is responsible for what they land.

**Their job is supervision, not project management.** They are not moving cards. Jira and Linear
exist for people *doing* work; this exists for someone *watching* work being done by processes that
fail silently. Their questions, in the order they ask them:

1. **Does anything need me right now?** — expired leases, escalations, blocker findings
2. **Is anything being closed dishonestly?** — forced transitions, `done` with no independent verification
3. **What happened while I was away?** — the event journal since a cursor
4. **How far along is this?** — rollups
5. **What exactly is an agent looking at?** — the brief, verbatim

A dashboard that answers 4 first — the burndown-chart instinct — would be answering the least
urgent question with the most screen space.

## 2. The idea the whole design hangs on

**The agents already read a fixed-width text rendering.** Doc 06 defines it precisely: `ref` leads
every row, columns are aligned, absent means normal, and the attention block ends every result.

> **So the dashboard shows that same text, verbatim, and adds only what a human needs and an agent
> does not: time, history, and navigation.**

Human and agent look at **one artifact**. When an operator wonders "what is the agent seeing?", the
answer is on screen, not reconstructed. No other tool in this category can do that, because no other
tool's payload is already a rendered board.

Everything below follows from that. The UI is a **reading room for machine-facing text**, with
supervision affordances around the edges.

## 3. Color — the product's own rule, applied to the interface

Doc 06 rule 4: **"Absent means normal. Only exceptions get a marker."** Carry it straight into the
UI:

> **The interface is monochrome. Saturated colour appears only where something needs a decision.
> There is no brand accent.**

A calm system is **grey**. That is the opinionated choice here, and it is the deliberate divergence
from the reference: `FounderOS` is a phosphor-green command deck where the accent is everywhere and
means "this product". Here, colour is scarce and each hue means exactly one thing:

| Token | Means | Where it may appear |
|---|---|---|
| `--live` · cool blue | a claim is held **right now** | lease clocks, active rows |
| `--attention` · amber | needs a human decision | the attention queue, stale leases |
| `--refused` · red | a blocker, a refusal, a **forced** transition | findings, the honesty strip |
| `--verified` · green | evidence exists and someone other than the author ran it | verification ticks only |

**Green is rationed hardest.** In this product "done" is cheap and "verified" is not — 424 of 502
checkboxes in the source corpus were ticked in runs their ledgers called finished. Green must never
mean "someone said so".

### Palette

```
dark (default)                    light
--bg          #0a0a0b             #f2f1ee
--surface     #101013             #fbfaf8
--surface-2   #17171b             #eceae5
--border      #23232b             #d9d6cf
--hairline    rgba(255,255,255,.06)  rgba(0,0,0,.07)
--text        #e9e9ec             #1c1b19
--text-2      #9b9ba4             #6a675f
--text-3      #5e5e69             #97938a
--live        #6aa8f5             #2f6fd0
--attention   #f0b429             #b07600
--refused     #f4776c             #c2402f
--verified    #58c98a             #2f7d52
```

Near-black, but **cool and untinted** — not the reference's green-shifted black, because a hue in
the background would compete with the four that carry meaning. Light theme is warm paper, so the
two skins read as *night shift* and *day shift* rather than as a colour inversion.

## 4. Type — two families, and the split carries meaning

| Family | Used for | Why |
|---|---|---|
| **IBM Plex Mono** | anything the **agent also sees**: rows, refs, commands, result lines, the verbatim brief, the refusal | the alignment *is* the information; a proportional face destroys it |
| **IBM Plex Sans** | the app's own chrome: navigation, labels, timestamps, human prose | prose set in mono is slower to read, and the reference's blanket-mono is a cost it pays for style |

**The typeface tells you whose artifact you are reading.** Mono means *this is what the machine
produced*; sans means *this is the app talking about it*. Designed as a pair, industrial provenance,
and not Inter.

A corollary that keeps it honest: **the agent's text keeps its capitals** — `NEXT`, `BINDING`,
`REFUSED` — because it is quoted verbatim. **The UI's own labels are sentence case.** Never add a
tracked-out all-caps eyebrow; the caps in this interface are always a quotation.

Scale: 12 / 13 / 15 / 18 / 24 / 32, 1.5 line-height for sans prose, 1.45 for mono blocks.
Mono blocks never wrap — they scroll horizontally, because a wrapped column is a broken column.

## 5. Layout

Left-aligned throughout. **Nothing is centred**: centring column-aligned data destroys the only
visual structure the payload has.

### The landing view — the watch floor

Answers "does anything need me?" without a click.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ workstate                                        gaia · nomos · nomos-word  │  ← projects, not tabs
├─────────────────────────────────────────────────────────────────────────────┤
│  WORKING NOW                                                                │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │ ● orchestrator-1   entity-store/3.1   Port the DSS mapper      18:42   │  │  ← lease clock
│  │ ● fix-B-07         prod-log-triage/2  Where-clause rejection   04:11   │  │     counts DOWN
│  │ ○ wsu4-guards      —  lease expired 13m ago                   −13:02   │  │  ← amber, past zero
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
│  NEEDS YOU                                              7                   │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │ blocker   entity-store/F-07   ContentHash written before the work …   │  │
│  │ stale     entity-store/2.8    claim expired 13m ago                   │  │
│  │ revisit   entity-store/A3     DSS jsonexport reachable — 3.1 closed   │  │
│  └───────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
│  HONESTY                                                                    │
│  104 closed unverified  ·  0 forced  ·  2 ungated  ·  119 follow-ups open   │
└─────────────────────────────────────────────────────────────────────────────┘
```

The honesty strip is one line and it is **the reason this product exists**. It is not a chart. It
is a count that should be zero and usually is not. The numbers above are **the real corpus**,
measured — `honesty` on `/api/watch` returns exactly these four fields.

**The fourth is deliberately unlike the other three.** `doneUnverified`, `forced` and `ungated` are
accusations. `followupsOpen` is not: a deferral that became a tracked follow-up is doc 07 §4 working
as designed, and 119 of them is the system telling the truth rather than failing. It is on the strip
because **a panel that only ever shows what went wrong is one people stop reading** — and colour
follows that distinction, since §3 spends colour only where a decision is needed.

### The run view — the reading room

```
┌──────────────┬──────────────────────────────────────┬───────────────────────┐
│ entity-store │  RUN entity-store · project Gaia     │  JOURNAL              │
│              │  GOAL GAIA gains a second …          │  11:24 1.1 → done     │
│ ▸ 1  done    │                                      │  11:22 verified 12/12 │
│ ▸ 2  done    │  WHERE  phase 3 of 10. 59 of 137 …   │  11:19 claimed 30m    │
│ ▾ 3  open    │  NEXT   work_get(ref:"3.1") …        │  11:02 F-07 raised    │
│   3.1 ready  │                                      │                       │
│   3.2 open   │  BINDING (37)                        │  ── since you left ── │
│   3.R1 done  │    ! Never connect to gaia.factory…  │  09:41 2.8 forced     │
│ ▸ 4  done ⚠  │                                      │                       │
│ ▸ 9  open    │  ── attention (5) ─────────────────  │                       │
│              │  ⚠ 9.2a done, no orchestrator verify │                       │
└──────────────┴──────────────────────────────────────┴───────────────────────┘
```

- **Centre is verbatim.** The exact bytes `work_brief` returned. Selectable, copyable.
- **Left is navigation and only navigation** — the tree with its rollup, and the one glyph that
  earns its place: `⚠` on a phase whose status disagrees with its children.
- **Right is time**, which the agent's view has none of: the event journal, with a *since you last
  looked* divider driven by `event.seq`.

### The rollup disagreement is a feature, and the real data has it

`phase-4` is `done` with **10 of 23** children done; `phase-6` is `done` with **0 of 9**. Both came
from gate lines in the original ledger. That is not an import bug — it is the corpus admitting
something, and the markdown hid it for months.

**So the tree shows both numbers and marks the disagreement.** A UI that displayed only the status
would be laundering exactly the dishonesty the product exists to surface.

## 6. Motion — one thing moves

**Lease clocks tick.** Nothing else animates on its own.

New journal rows appear without a slide-in; the *divider* moves instead. Motion in a supervision
console is a claim that the world changed, so it is spent only where the world actually did.
`prefers-reduced-motion` freezes the clock to a static remaining-time string.

## 7. Plan reviewed against the brief

Working through the generic-default checklist honestly:

| Cluster | Verdict |
|---|---|
| cream + serif + terracotta | not used |
| near-black + one bright acid accent | **near-black yes — but explicitly NO single accent.** Colour is scarce and semantic; a healthy system is entirely grey. This is the deliberate divergence from the pinned reference. |
| hairlines, zero radius, dense columns | **used, and it is the brief's pinned direction** — the owner named `FounderOS-DEMO`. Also the honest form for column-aligned payload. |
| identical rounded cards + soft shadows | not used — dense rows, hairline dividers, no shadows |
| ALL-CAPS eyebrows, `·` chrome, `→` on buttons | **avoided in UI chrome.** Capitals and `·` appear only inside quoted agent text, where they are the artifact. |

**What makes this design belong to this project and no other:** the centre pane is the machine's own
artifact rather than a re-rendering of it; colour is governed by the product's own "absent means
normal" rule; and the landing view leads with a lease clock and an honesty count — two things that
only matter when your workers are processes that can die mid-sentence without saying so.

## 8. Build notes

- **SolidJS + Vite + Tailwind 4**, `@tailwindcss/vite`, tokens as CSS vars under
  `:root[data-theme]`, exactly the shape `Nomos.Client/nomos` uses.
- Served from `wwwroot` by `WorkState.Api` — **`UseStaticFiles` + `MapFallbackToFile` are not wired
  yet** (confirmed by grep). Doc 08 §4.
- Data: the App API over cookie auth, **never `/mcp`** — two schemes that never cross.
- Live: `EventSource` on `/api/stream`, fed by Postgres `LISTEN/NOTIFY` on `event.seq`. The board
  updates because the **database** changed, not because a session is open.
- The centre pane calls the same `BoardReader` the MCP tools use. **One query layer, two
  presentations** — if they ever disagree, the human is being shown something the agent is not.
