# Ledger — malformed-run

> Authored for the suite, not copied from the corpus: it holds the shapes the importer must degrade to
> prose rather than crash on, and each one is asserted by name in `ImportUnmappedTests`.

## Task log

| Task | Verdict | Commit | Verify | Note |
|---|---|---|---|---|
| 1.1 the row that fits | ACCEPTED | `1a2b3c4d5e6` | `dotnet test --filter A` 3/3 | fine |
| 1.2 the row with a missing cell | ACCEPTED | `2b3c4d5e6f7` |
| 1.3 the row with one cell too many | ACCEPTED | `3c4d5e6f708` | `dotnet test --filter C` 1/1 | fine | and one more |

## The table with no header at all

| 2.1 a row whose header was deleted |
| 2.2 another one |

## Open assumptions

| # | Assumption | Evidence level | Reversal cost | Revisit when |
|---|---|---|---|---|
| A1 | the evidence column says something the vocabulary does not know | vibes | — | never |

## A grid that is prose

| environment | value |
|---|---|
| shell | PowerShell |
| docker | running |
