# Spec — malformed-run

Authored for the suite. Its content must never reach a `doc_block`: `docs/workstate/00-analysis.md` §7
keeps a spec in git, referenced by path and sha, because two copies of a spec are the failure the store
exists to remove. `ImportSpecTests` asserts the artifact row and the absence of the doc.

## §1 A section the importer must not read

If this sentence is searchable in the store, the trap closed on us.
