# PROBE — what does Claude Code actually surface of an MCP server?

Task **0.2** of `docs/workstate/03-stack-and-plan.md`, the last unverified premise in the design.
Half of `docs/workstate/04-self-explaining.md` rests on the answers.

This server is **not** WorkState. It has no state, no database and no `work_*` tools. It publishes six
primitives, each labelled with the question it exists to answer, so that reading a client's rendering
tells you which channel got through.

> **The "observed" column is filled in by whoever runs the probe — not by the unit that built it.**
> A session cannot register an MCP server with itself and then read its own system prompt.

---

## 1. Run it

```bash
docker compose -f mcp/compose.dev.yml up -d      # not needed by the probe; the probe touches no DB
dotnet run --project mcp/WorkState.Api           # http://localhost:5290, Development profile
```

The Development profile ships the two tokens in `mcp/WorkState.Api/appsettings.Development.json`:
`dev-reader-token` and `dev-writer-token`. They are throwaway and localhost-only.

## 2. Register it

**Reader** — the profile most of the questions are asked with:

```bash
claude mcp add --transport http workstate-probe http://localhost:5290/mcp \
  --header "Authorization: Bearer dev-reader-token"
```

**Writer** — register a second time under a different name to compare the two `tools/list` payloads:

```bash
claude mcp add --transport http workstate-probe-writer http://localhost:5290/mcp \
  --header "Authorization: Bearer dev-writer-token"
```

Then, in a **fresh** session with no skill loaded, say only:

> use the workstate-probe server and report exactly what you see for each probe question

Remove them afterwards with `claude mcp remove workstate-probe` /
`claude mcp remove workstate-probe-writer`.

## 3. Greppable markers

Each channel emits exactly one marker and nothing else emits it, so a transcript search is decisive.
They are defined once in `mcp/WorkState.Abstractions/Probe/ProbeTokens.cs`.

| Marker | Emitted by | Means |
|---|---|---|
| `WSPROBE-SI-7F3A9C` | `ServerInstructions` | the instructions reached the model |
| `WSPROBE-TEXT-A1B2C3` | `probe_echo` → `content[0]` | the compact text block reached the model |
| `WSPROBE-JSON-D4E5F6` | `probe_echo` → `structuredContent` | the JSON reached the model |
| `WSPROBE-LINK-11A2B3` | `probe_link` → `resource_link.title` | a tool's resource link was rendered |
| `WSPROBE-PROMPT-22C4D5` | `probe_prompt` | a server prompt was rendered |
| `WSPROBE-RES-FIXED-33E6F7` | `work://probe/fixed` | a fixed resource was read |
| `WSPROBE-RES-TMPL-44A8B9` | `work://probe/{id}` | a templated resource was read |
| `WSPROBE-COMPLETE-55CA1B` | `completions/complete` on `{id}` | the client asked for completions |
| `WSPROBE-WRITER-66DC2E` | `probe_writer_only` | a writer-scoped tool ran |

## 4. The questions, and what to look for

### Q1 — `probe_echo`: text, JSON, or both? And is there an `outputSchema`?

`probe_echo` returns a hand-built `CallToolResult`: one compact text line in `Content`, the full object
in `StructuredContent`, **deliberately not duplicated** (`docs/workstate/01-mcp-surface.md` §2). The
attribute still declares `UseStructuredContent = true, OutputSchemaType = typeof(ProbeResult)`.

*Look for:* which of `WSPROBE-TEXT-A1B2C3` / `WSPROBE-JSON-D4E5F6` the model can quote back. Both →
the client shows both and §2's saving is not real. Text only → the JSON never reaches the model and
`structuredContent` is for the dashboard alone. JSON only → the compact rendering is wasted effort.
Also check `/mcp` or the tool's schema view for a non-null `outputSchema`.

*Already proven in `dotnet test`:* the schema **is** published and the two halves **are** different
over the wire. What the test cannot see is what the client does with them.

### Q2 — `probe_link`: is a resource link rendered, or followed?

Returns a text line plus a `ResourceLinkBlock` pointing at `work://probe/fixed`.

*Look for:* does the model see a followable link, an inlined body, or nothing? This decides
`docs/workstate/01` §2's "anything large comes back as a resource_link" — the mechanism that makes
`work_get` on a 400-line brief cost 40 tokens instead of 6,000.

### Q3 — `probe_prompt`: do server prompts exist in this client, and how are they invoked?

One prompt, one **required** argument `topic`.

*Look for:* a slash command, an `@`-mention, a picker — or nothing at all. And whether `topic` was
collected. This is the channel `docs/workstate/04` §5 calls the least certain; if prompts do not
surface, doc 01 §9's seven prompts move into `work_brief` output and tool descriptions.

### Q4 — `work://probe/fixed`: do static resources appear?

*Look for:* whether the resource is listed, whether it can be attached with `@`, and whether attaching
it puts `WSPROBE-RES-FIXED-33E6F7` in context. Doc 01 §8 assumes `work://workspace/{slug}/brief` is
attachable this way.

### Q5 — `work://probe/{id}`: do templates appear, and is `{id}` completed?

The server answers `completions/complete` for `{id}` with `alpha`, `beta`, `gamma` and
`WSPROBE-COMPLETE-55CA1B`.

*Look for:* the template in any resource picker, and whether typing an id offers those candidates.
Completion is what would give a human a live item picker for free (doc 01 §8).

> **Watch for shadowing.** `work://probe/fixed` also matches `work://probe/{id}`. Which one answers a
> read of that exact URI is itself worth recording — the real design has the same overlap between
> `work://workspace/{slug}` and its sub-paths.

### Q6 — `probe_writer_only`: hidden from the list, or only refused on call?

Carries `[Authorize(Policy = "workstate-writer")]`, honoured by `AddAuthorizationFilters()`.

*Look for:* register both tokens (§2) and compare the two servers' tool lists. Is
`probe_writer_only` absent from the reader server's list, or present and refused on call? Doc 01 §6
wants the list to narrow; if `[Authorize]` alone does not narrow it, that is what `WithListToolsHandler`
is for, and U3 must wire it. Also check whether Claude Code **caches** `tools/list` across tokens.

### Q0 — `ServerInstructions`

*Look for:* `WSPROBE-SI-7F3A9C` in the session. Claude Code is expected to render server instructions
under an **"MCP Server Instructions"** heading. Record whether it appears, under what heading, and
whether it is truncated — `docs/workstate/04` §1 spends its entire budget on this channel and caps it
at 40 lines on the assumption that it is always in context.

---

## 5. Observations

| # | Question | Observed | Consequence for the design |
|---|---|---|---|
| Q0 | Does `ServerInstructions` reach the model, under what heading, truncated? | | |
| Q1 | Text, JSON, or both? Is `outputSchema` shown? | | |
| Q2 | Is a tool's `resource_link` rendered / followable? | | |
| Q3 | Do server prompts appear, and how are they invoked? | | |
| Q4 | Do fixed resources appear, and are they attachable? | | |
| Q5 | Do templates appear? Is `{id}` completed? | | |
| Q6 | Is a writer-only tool hidden from a reader's `tools/list`, or only refused on call? | | |

When this table is filled in, `docs/workstate/01` §9 and §6 and `docs/workstate/04` §5 get corrected
where they were wrong, and Phase 0 closes.
