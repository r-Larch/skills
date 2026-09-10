---
name: code-comments
description: >-
  Strip comments out of code down to the few that earn their place. A
  subtractive pass: the default verdict on every comment is DELETE, and a
  comment survives only when it states a present-tense constraint the code
  cannot state itself — typically an invariant imposed from another file, which
  it points at rather than paraphrases. Treats existing comments as untrusted
  (stale/wrong ones are deleted, not corrected) and budgets survivors to one
  line. Language-agnostic doctrine with worked mechanics for C#/.NET and
  TypeScript/JavaScript. USE FOR: "reduce/clean up the comments", "too many
  comments", a comment-noise pass over a project or feature folder, culling
  "used to" / changelog narration left behind by a refactor, stripping JSDoc or
  XML docs that restate a signature, fixing CS1587 from `/// <summary>` on
  record positional parameters. DO NOT USE FOR: writing new documentation,
  authoring XML docs or JSDoc for a public API surface, README/markdown prose,
  or any task that changes behaviour.
---

# Code comment cleanup

You are running a **subtractive** pass over source code. You remove comments. You do not
rewrite code, rename anything, or change behaviour. The build must be green when you
finish and the diff must contain no non-comment lines except where a deletion forces a
mechanical fixup (see *Mechanics*).

The premise: **the primary reader of this code is an AI agent, not a human newcomer.**
That reader already knows the language and every common library idiom — C#, ASP.NET Core,
EF Core, LINQ, DI and async; TypeScript, its type system, ES modules, promises, React or
Solid reactivity. It does not need to be taught. What it genuinely lacks is context from
files it does not currently have open. That gap — and only that gap — is what a surviving
comment fills.

The verdict rules below are the same in every language. The *Mechanics* section is where
the languages differ, and it is split accordingly.

---

## The verdict procedure

Read every comment against the code it sits on. Assign one of three verdicts.

**DELETE** — the default. If you find yourself weighing whether a comment earns its
place, that deliberation *is* the verdict. Delete it and move on. Do not think about it
twice.

**TRIM** — the comment names a real, present, non-obvious constraint, but says it in more
words than it needs. Cut it to one line.

**KEEP** — one line already, and it carries something the code cannot.

There is no fourth verdict. You never *improve* a comment into existence, never expand
one, and never convert a bad comment into a good one. If a comment is wrong, it is
deleted — rewriting it is authoring documentation, which is a different task.

### Delete these on sight

- **Restatement.** `// loop over the documents`, `// return the result`,
  `// inject the db context`. The code says it.
- **Teaching.** Anything explaining what a language or framework feature does — what
  `await` yields, what `IAsyncEnumerable` is for, how DI resolves a scope, what a primary
  constructor is; what `??=` does, why a `useEffect` cleanup runs, what a Solid signal is,
  how destructuring works. The reader knows.
- **History.** "used to", "previously", "before the refactor", "no longer", the name of a
  retired type/flag/method, or any narration of what a change did. Git holds the past.
  A comment about it is noise the reader parses and then discards, and it rots into a lie
  the next time the code moves.
- **Paraphrase of a decision that lives elsewhere.** Restating what an interceptor does,
  what a policy allows, what the options binder validates, what a sibling service
  guarantees. Two copies of one rule means one of them goes stale and then actively
  misleads. Point at the contract's home instead (see below).
- **Section banners and scaffolding.** `#region`-style dividers in comment form,
  `// ---- helpers ----`, `// constructor`, `// GET`, decorative rules.
- **Ceremony.** File headers, author/date stamps, copyright blocks that no build step
  requires, `// TODO` with no owner and no meaning, commented-out code.
- **Doc comments that restate the signature.** `/// <summary>Gets the name.</summary>` on
  `public string Name`; `/// <param name="id">The id.</param>`. In TS the same thing, and
  worse, because the types already say it: `/** @param userId The user id. */` above
  `function get(userId: string)`, or `/** Returns a promise. */` on an `async` function.
  A JSDoc block that carries no `@` tag the tooling reads, and no constraint the signature
  lacks, is restatement.
- **Anything already contradicted by the code.** See *Untrusted comments*.

One carve-out from "commented-out code": a commented-out `builder.HasIndex(...)` or
`migrationBuilder.Sql(...)` in an EF entity configuration is usually not dead code. It is
the only record of an index the live database has and the model cannot express. Leave it,
or ask.

### A comment may survive when it is

- **A cross-file invariant that binds this file.** Something enforced, assumed, or
  required elsewhere that a reader of *this* file alone would violate. This is the main
  legitimate case.
- **A "why not the obvious thing" note.** A workaround for a concrete bug, a deliberate
  ordering, an allocation avoided on purpose, a `catch` that must swallow. Name the
  cause, not the story.
- **A load-bearing external contract.** A wire format, a fixed magic value, a spec
  clause, a third-party quirk the types do not encode.
- **A machine-read comment.** Not prose — leave it, whatever it looks like.
  - .NET: `#pragma` directives, analyzer suppressions with a justification, `// lang=json`,
    source-generator markers.
  - TS/JS: `// @ts-expect-error` and `// @ts-ignore` (and their justification — an
    unexplained one is worth *reporting*, not deleting), `// eslint-disable*`,
    `// prettier-ignore`, `/// <reference types="…" />`, `/* webpackChunkName: "x" */` and
    other bundler magic comments, `/*! … */` license banners minifiers preserve,
    `//# sourceMappingURL=`, and JSDoc tags the toolchain actually consumes —
    `@deprecated`, `@ts-check`, `@type`/`@satisfies` in a `.js` file type-checked by TS.
  A `@deprecated` tag is an IDE affordance, not a comment: deleting it silently removes a
  warning every caller currently sees.

### Point, don't paraphrase

When a surviving comment concerns a rule owned by another file, it names the owner and
stops. The owner is the single source of truth; the comment is a pointer to it.

```csharp
// Bad — a paraphrase that will rot:
// TenantId is stamped automatically on insert when it is not already set, and
// validated on update and delete, so we must not set it here manually.

// Good — a pointer that cannot rot:
// TenantId is interceptor-owned; see MultiTenantEntitiesInterceptor.
```

### The line budget

A survivor gets **one line**. Spend a second only when the constraint is genuinely
irreducible, a third essentially never. Every line you keep is read by every agent that
opens the file for the rest of the project's life — treat lines as a budget you are
spending, not a canvas.

---

## Untrusted comments

**Never treat a comment as a source of truth.** Comments in an existing codebase are
frequently stale, and some are outright wrong. Read each one against the code beneath it.

- Comment agrees with the code, adds nothing → **delete** (it was redundant).
- Comment disagrees with the code → the *comment* is wrong → **delete**. Do not correct
  it, and do not change the code to match it.
- Comment disagrees with the code in a way that suggests the **code** is the broken half
  (a guard that looks inverted, a bound that looks off, a case the comment says is
  handled and is not) → delete the comment, leave the code alone, and **report it to the
  user**. Do not fix it inside a comment pass; a comment cleanup that silently changes
  behaviour is a bad diff.

Collect these into a short list and surface it when you report.

---

## Mechanics — every language

- **Never add a doc block.** This pass removes. If a public API surface needs docs, that is
  a separate, explicitly requested task.
- **Ask before culling a documented public surface.** Docs on a published package, a
  partner-facing endpoint, or anything feeding generated documentation or client codegen
  may have consumers you cannot see. Confirm scope first; strip internal ones freely.
- **Preserve compilable structure.** A deletion must not leave a dangling conditional, an
  orphaned decorator or attribute, an empty block that changes meaning, or a suppression
  whose restore you removed.
- **Clean the whitespace you create.** A deleted comment that leaves a blank line doubling
  an existing one takes the blank line with it. Do not otherwise reflow code.
- **Localized and user-facing strings are not comments.** In a project whose UI, validation
  messages or tool descriptions are written in another language, those live in attributes,
  decorators and string literals — leave them entirely alone.

## Mechanics — C# / .NET

- **`record` positional parameters take no `/// <summary>`.** XML docs on the positional
  parameter list of a record produce **CS1587** ("XML comment is not placed on a valid
  language element"). Delete them wherever they appear. Documenting a record's positional
  members legitimately means `/// <param name="X">` on the record declaration itself — but
  under this skill's default that is authoring docs, so only do it if the user asked.
- **A commented-out `builder.HasIndex(...)` or `migrationBuilder.Sql(...)`** in an EF entity
  configuration is usually not dead code. It is often the only record of an index the live
  database has and the model cannot express. Leave it, or ask.
- **Watch the preprocessor.** Removing a comment must not strand an `#if`/`#endif` or a
  `#pragma warning restore` whose `disable` you deleted.

## Mechanics — TypeScript / JavaScript

- **The types are the documentation.** Most JSDoc in a `.ts` file is restatement by
  construction: the signature already carries the names, the types, the optionality and the
  return. A JSDoc block survives only for what a type cannot say — a unit, a range, a
  required call order, an external contract.
- **A `.js` file may be type-checked *by* its JSDoc.** `@type`, `@param`, `@returns`,
  `@satisfies` and `@ts-check` in a plain `.js` file under `checkJs` are load-bearing types,
  not prose. Deleting them is a type-level behaviour change. Check `tsconfig.json` for
  `checkJs`/`allowJs` before touching JSDoc in `.js`.
- **Directives are not comments.** `"use client"`, `"use server"`, `"use strict"` are string
  literal statements and must not be touched, even though they read like annotations.
- **JSX comments are `{/* … */}`.** Deleting the comment leaves an empty expression container
  `{}` — remove the braces too, and mind that `{" "}` next to it is a meaningful space, not
  scaffolding.
- **Never strip a banner a build step depends on.** `/*! … */` survives minification by
  convention and is often a licence obligation.
- **Typecheck, not just build.** `pnpm run ts` / `tsc --noEmit` is the green this pass needs;
  a bundler may not re-check types at all.

## Working order

1. Read the repo's own conventions first (`CLAUDE.md`, `AGENTS.md`,
   `.github/instructions/*`). A project rule about comments overrides this skill's
   defaults; a project rule about *anything else* tells you which cross-file invariants
   are real and therefore which comments are legitimately load-bearing. In a mixed repo the
   backend and frontend often have separate instruction files with different rules — read
   the one that governs the files you are about to touch.
2. Work **one file at a time**, in whole files. Never regex-strip comments across a tree —
   the verdict requires reading the code under each one.
3. Batch by folder/feature so the diff stays reviewable.
4. After each batch, run whatever *this* project calls green — a build, `tsc --noEmit`,
   a lint that fails on unused directives. No behaviour delta.
5. Report: files touched, rough comment count before/after, every comment kept and why
   (one line each — the keeps are the interesting part of the diff), and the list of
   suspected code bugs found via contradicting comments.
