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

Run **2026-09-18** against Claude Code. Observer session `wsprobe-observer` (`5b13092e`), read-only.
The server side was independently green first — `dotnet test` 9/9 proves the server *sends* all of
this over the real transport. Everything below is therefore about the **client**, not the server.

| # | Question | Observed | Consequence for the design |
|---|---|---|---|
| Q0 | `ServerInstructions` reach the model? | **YES.** Under top-level `# MCP Server Instructions`, sub-heading `## workstate-probe`. **Complete, not truncated** — all 15 non-blank lines. Appears **once per registered server**. | `docs/workstate/04` §1 holds. The budget is **per registration**, so ≤40 lines is right. **The strongest channel we have.** |
| Q1 | Text, JSON, or both? | **JSON ONLY.** `WSPROBE-JSON-D4E5F6` arrived; `WSPROBE-TEXT-A1B2C3` **never did** — absent, not truncated. `probe_link`'s text line *did* arrive, and it carries no `structuredContent`. | ⛔ **`structuredContent` REPLACES `content`.** This **invalidates `docs/workstate/01` §2** as written. See §6. |
| Q1b | Is `outputSchema` visible to the model? | **NO.** The model sees description + input parameters only. | `OutputSchemaType` serves validating clients, not the model. Spend no explanation budget there. |
| Q2 | Is a `resource_link` rendered / followable? | **PARTIAL.** Rendered as a labelled bare URI in text: `[Resource link: probe_fixed] work://probe/fixed`. No followable affordance, nothing inlined. Retrievable in a second step via `ReadMcpResourceTool` — but only by an agent that already knows that tool exists. | The saving is **real but not free**: the tool description must name the follow-up call, or the link is a dead end. |
| Q3 | Do server prompts appear? | **NO. Invisible in every channel** — absent from the tool list and the skills listing, not invokable (`Unknown skill`). `WSPROBE-PROMPT-22C4D5` unobtainable. | ⛔ **`docs/workstate/01` §9's seven prompts are dead weight in this client.** Their content moves into tool descriptions and `work_brief` output. `04` §5 called this the least certain channel; it was right. |
| Q4 | Do fixed resources appear? | **YES.** Listed via `ListMcpResourcesTool`, readable, `WSPROBE-RES-FIXED-33E6F7` obtained. Identical on both servers. | `01` §8's fixed projections work. Keep them. |
| Q5 | Templates? Completion? | **PARTIAL.** The template is **not listed anywhere** — `resources/list` returns the fixed resource only. But `work://probe/alpha` **reads fine if guessed**: `WSPROBE-RES-TMPL-44A8B9`. **Completion: no client mechanism at all** (`ReadMcpResourceDirTool` → *"Directory listing is not enabled in this build"*). | Templates are **readable but undiscoverable** — useful only when the model was handed the exact URI by a tool result or `ServerInstructions`. **Drop the completions plan** (`01` §8). |
| Q5b | Shadowing: which answers `work://probe/fixed`? | **The fixed resource**, not the template. | Settles `work://workspace/{slug}` vs its sub-paths: **exact matches win.** |
| Q6 | Writer-only tool: hidden, or refused on call? | **HIDDEN.** Reader sees 2 tools, writer 3. The reader could not even attempt a call — the harness refused locally (*"No such tool available"*), so no request reached the server. | ✅ **`01` §6 works as designed, with `[Authorize]` alone. `WithListToolsHandler` is NOT needed** — U3 is simpler than planned. Lists are per-registration, so no cross-token cache issue. |

---

## 6. ⛔ The finding that changes the design

**`structuredContent` replaces the text block rather than accompanying it.**

`probe_echo` returns both; the model received only the JSON. `probe_link` returns text and **no**
`structuredContent`, and its text arrived verbatim. That rules out general text suppression and makes
the rule specific: **when `structuredContent` is present, `content` is dropped.**

`docs/workstate/01` §2 chose *"compact text in `content`, JSON in `structuredContent`, deliberately
not duplicated"* to halve the token cost of the most-called tool. **As written, that makes the compact
rendering invisible to the model** — and with it the `attention` block, which is the nag channel the
entire forced-closure story leans on.

The server is not at fault: the suite proves both halves go out on the wire correctly. This is a
client rendering rule, and it is exactly the class of fact that cannot be found by reading a
specification. **This is why the probe ran before the product code.**

### Remedies — settled by probe round 2

| | Shape | Cost |
|---|---|---|
| **A** | **Text only.** No `structuredContent` on read tools; the compact rendering is the product. | Loses typed output for programmatic clients — but the dashboard uses the App API, not MCP, so the loss may be zero. |
| **B** | **Render inside the JSON.** `structuredContent = { "render": "<compact text>", "items": [...] }` | Model reads `render`, machines read `items`. One payload, both audiences. |
| **C** | Duplicate into both, per the spec's back-compat `SHOULD`. | Pays exactly the token cost §2 existed to avoid. |

**U3 must not build a renderer until this is settled.**
