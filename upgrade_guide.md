# Upgrading from 2.x to 3.0

**You need this if you use any of the planning or agent skills.** rlarch 3.0 keeps only
`dotnet-reflect`, `dotnet-source`, `ts-source` and `code-comments`. The other six skills and the
`agents` MCP server moved to the `workman` plugin in
[github.com/getsupraman/workman](https://github.com/getsupraman/workman), under new names:

| rlarch 2.x | workman |
|---|---|
| `/rlarch:workstate` | `/workman:plan` |
| `/rlarch:workstate-setup` | `/workman:setup` |
| `/rlarch:workstate-import` | `/workman:import` |
| `/rlarch:controller` | `/workman:controller` |
| `/rlarch:orchestrate` | `/workman:orchestrate` |
| `/rlarch:nested-claude` | `/workman:agents` (tools `mcp__plugin_workman_agents__*`) |

1. Install workman, then restart Claude Code:

   ```bash
   claude plugin marketplace add getsupraman/workman
   claude plugin install workman@supraman
   ```

2. Update rlarch: `claude plugin marketplace update rlarch` and `claude plugin update rlarch@rlarch`.
   Confirm with `claude plugin details rlarch`: version `3.x`, four skills.

Your Workman server, its `work_*` tools and your data stay exactly where they are; there is nothing
to re-run. To start fresh instead, `/workman:setup` self-hosts a server, or connect to the hosted one
at [work.supraman.ai](https://work.supraman.ai) (by invitation) as described in the
[README](README.md#looking-for-orchestrate-controller-or-nested-claude).

---

# Upgrading to the single `rlarch` plugin

**You need this if `claude plugin list` shows `dotnet-reflect@rlarch` or `dotnet-source@rlarch`.**
Those were separate plugins before 1.0; today there is one, `rlarch`. If you don't see them, you're
a new user — go to the [README](README.md).

## Read this first: the two plugins are probably installed at *different* scopes

`claude plugin uninstall` **defaults to `--scope user`**, so uninstalling a plugin that lives at
project scope silently finds nothing and leaves you half-migrated. Check first:

```bash
claude plugin list
```

Read the `Scope:` line under each `@rlarch` entry and use that value below. If you get it wrong, the
CLI names the scope the plugin is actually installed at — that error is a redirect, not a failure.

## The upgrade — uninstall → update → install

A plugin dropped from a marketplace *stays installed*, so uninstall while the marketplace still lists
the old entries.

```bash
# 1. uninstall — substitute the scopes from `claude plugin list`
claude plugin uninstall dotnet-reflect@rlarch --scope project
claude plugin uninstall dotnet-source@rlarch  --scope user

# 2. refresh the marketplace (or `claude plugin marketplace add r-Larch/skills` if you never added it)
claude plugin marketplace update rlarch

# 3. install the one plugin
claude plugin install rlarch@rlarch --scope user
```

Restart Claude Code, then verify with `claude plugin details rlarch`: one `rlarch@rlarch` entry and
no `dotnet-reflect@rlarch` / `dotnet-source@rlarch` lines left.

## The skills are namespaced now

| Before | After |
|---|---|
| `/dotnet-reflect` | `/rlarch:dotnet-reflect` |
| `/dotnet-source` | `/rlarch:dotnet-source` |

The prefix only applies when you *type* a slash command — automatic invocation is unaffected, so in
day-to-day use you rarely type the name at all. There is no setting that turns the namespace off.

## Troubleshooting

- **A skill shows up twice.** You have the plugin and a hand copy in `~/.claude/skills/`. Delete the
  hand copy and restart.
- **`dotnet-reflect@rlarch` is still listed after the marketplace update.** Expected — run the
  uninstall with the scope `claude plugin list` reports.
- **Uninstall reports nothing to remove.** Wrong scope; pass `--scope project` (or `local`).
- **Fewer skills than expected.** The marketplace is stale: `claude plugin marketplace update rlarch`,
  reinstall, restart.
