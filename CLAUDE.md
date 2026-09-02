# CLAUDE.md

The guidance for this repo lives in **[AGENTS.md](AGENTS.md)** — one file, so Claude Code, Codex,
Cursor and anything else that reads a repo contract all get the same rules. **Read it before making
a change.**

The one rule most easily missed, repeated here because skipping it silently ships nothing:

> **Any change that must reach an installed user requires bumping `version` in
> `plugins/rlarch/.claude-plugin/plugin.json`.** The updater gates on that field and caches the
> payload per version. Push without a bump and `/plugin` reports *"already at the latest version"*,
> `/skills` reports *"No changes"*, and your commit reaches nobody.
