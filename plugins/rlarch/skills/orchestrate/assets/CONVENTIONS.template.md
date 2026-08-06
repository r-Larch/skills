# Conventions — <undertaking name>

> Written **once** in Phase 0. Every worker and reviewer brief points here:
> "Read `.claude/orchestration/<slug>/CONVENTIONS.md` first — it is binding."
> Nothing in this file gets restated inside a brief. If you find yourself
> re-typing a constraint into a second brief, it belongs here instead.

## Repo guidance
Read before writing: `<CLAUDE.md / AGENTS.md / .github/instructions/**>`

## Stack facts workers shouldn't have to rediscover
- Language / framework / package manager: <…>
- Where things live: <src layout, test layout, generated code>
- Existing patterns to follow rather than reinvent: <path — what it exemplifies>

## Verification commands
| Purpose | Command |
|---|---|
| Build | `<…>` |
| Typecheck | `<…>` |
| Filtered test (use this during tasks) | `<… --filter <pattern>>` |
| Full suite (gates and close-out only) | `<…>` |
| Lint / format | `<…>` |

Read the actual result line, not an exit code from the end of a pipe.

## Generated code — never hand-edit
<paths / globs, and the command that regenerates them>

## Safety rules — binding on every task
- Do not commit or push. Leave changes in the working tree.
- Do not delete any file not named in your brief's scope. Report and stop instead.
- Do not add dependencies.
- Do not touch: <secrets, credentials, `.env`, migration history, production config>
- <repo-specific hazards — e.g. "never run the suite unfiltered, it hits a live service",
  "migrations are applied by hand in this repo", "this table is append-only">

## Domain rules that have burned us before
<Standing rules the user has stated. These are worth more than anything generic:
e.g. "never assume how <external system> behaves — probe it read-only first".>

## Out of scope for the whole undertaking
<The plan's non-goals, so no worker or reviewer drifts into them.>
