# Upgrading from 1.x to 2.x

**You need this if `claude plugin details rlarch` reports a 1.x version** and you use
`orchestrate` or `controller`. The full list of changes is in [CHANGELOG.md](CHANGELOG.md).

## What changes for you

- **`orchestrate` and `controller` now require the WorkState server** — a .NET service plus a
  Postgres, running in Docker on your own machine. They keep their plan, rules, evidence and record
  there and nowhere else. There is **no markdown fallback**: they no longer write `PLAN.md` /
  `LEDGER.md`, and if the `work_*` tools are missing they stop and point you at
  `/rlarch:workstate-setup`.
- Runs you started under 1.x as `PLAN.md` / `LEDGER.md` files are not read by the 2.x skills. The
  files stay where they are. Finish those runs before upgrading, or start them again under 2.x; the
  repo's one-way importer (`mcp/tools/WorkState.Import`) can load an old corpus into the store as a
  record.
- WorkState also serves an operator dashboard at **http://127.0.0.1:5290**, where you can watch a run.

## What keeps working without WorkState

`dotnet-reflect`, `dotnet-source`, `ts-source`, `code-comments` and `nested-claude` never touch
WorkState. If those are all you use, update the plugin and stop here — no Docker needed.

## What you must do

1. **Update the plugin** and restart Claude Code:

   ```bash
   claude plugin marketplace update rlarch
   claude plugin update rlarch@rlarch
   ```

   Confirm with `claude plugin details rlarch`: version `2.2.0`, eight skills.

2. **Run `/rlarch:workstate-setup` once.** It needs Docker running (Docker Desktop, or the Docker
   engine on Linux; Colima or OrbStack on macOS) and **no clone** (since 2.3.0). It runs a shipped
   script that writes `~/.workstate/.env` with random values, pulls and starts the published image
   `ghcr.io/r-larch/workstate-api`, registers the server with `claude mcp add --scope user`, and
   counts the tools the server returns — no token passes through the model. A machine already
   running WorkState from a clone keeps it with `--clone <path>`. The stack restarts with Docker, so
   this is once per machine, not once per session.

3. **Restart the session.** MCP servers are resolved at session start, so the `work_*` tools appear
   in the *next* session, not the one that ran the setup. In the new session, `work_brief` should
   answer.

If you registered WorkState by hand during 2.x testing with `http://localhost:5290/mcp`, re-register
it with `http://127.0.0.1:5290/mcp` (the setup script with `--force` does it): the server binds IPv4 loopback only, and some clients try `::1`
for `localhost` first.

---

# Upgrading to the single `rlarch` plugin

The `rlarch` marketplace used to ship **two** plugins, `dotnet-reflect` and `dotnet-source`. It now
ships **one**, `rlarch`, which contains those two as skills plus a third, `orchestrate`, that was
never installable before.

**You need this guide if `claude plugin list` shows `dotnet-reflect@rlarch` or `dotnet-source@rlarch`.**
If it doesn't, you're a new user — go to the [README](README.md) and install `rlarch@rlarch`.

Budget two minutes.

---

## Read this first: the two plugins are probably installed at *different* scopes

This is the one thing that goes wrong. `claude plugin uninstall` **defaults to `--scope user`**, so an
uninstall of a plugin that lives at project scope silently finds nothing and you end up half-migrated.

On the machine this guide was written against:

| Plugin | Scope |
|---|---|
| `dotnet-reflect@rlarch` | **project** |
| `dotnet-source@rlarch` | **user** |

**Check yours before you type anything else:**

```bash
claude plugin list
```

Read the `Scope:` line under each `@rlarch` entry and use that value in step 1. If you get it wrong the
CLI tells you which scope the plugin is actually installed at and which `--scope` to pass instead —
that error is a redirect, not a failure.

---

## The upgrade

**Order matters: uninstall → update → install.** A plugin dropped from a marketplace's `plugins`
array *stays installed* — refreshing the marketplace does not remove it — so uninstall while the
marketplace still lists the old entries and they resolve cleanly.

### 1. Uninstall the two old plugins

CLI — substitute the scopes you read from `claude plugin list`:

```bash
claude plugin uninstall dotnet-reflect@rlarch --scope project
claude plugin uninstall dotnet-source@rlarch  --scope user
```

In-session:

```
/plugin uninstall dotnet-reflect@rlarch
/plugin uninstall dotnet-source@rlarch
```

The in-session `/plugin` commands drive the interactive plugin UI, where the scope is shown to you
rather than passed as a flag. When the scopes differ — and here they do — the CLI form is the
unambiguous one. Use it if the UI leaves you guessing.

### 2. Update the marketplace

CLI:

```bash
claude plugin marketplace update rlarch
```

In-session:

```
/plugin marketplace update rlarch
```

If you never added the marketplace at all: `claude plugin marketplace add r-Larch/skills` (or
`/plugin marketplace add r-Larch/skills`).

### 3. Install the one plugin

CLI:

```bash
claude plugin install rlarch@rlarch --scope user
```

In-session:

```
/plugin install rlarch@rlarch
```

`--scope` accepts `user`, `project`, or `local` and defaults to `user`. Use `--scope project` if you
want the plugin declared in the repo you're standing in rather than for your whole account.

Restart Claude Code afterwards — plugin changes apply to a new session (`claude plugin update --help`
says as much: *restart required to apply*).

### 4. Verify

```bash
claude plugin list
claude plugin details rlarch
```

You want: exactly one `rlarch@rlarch` entry, no `dotnet-reflect@rlarch` or `dotnet-source@rlarch`
lines left, and `details` reporting **three** skills — `dotnet-reflect`, `dotnet-source`,
`orchestrate`.

---

## What changed for you: the skills are namespaced now

A skill that ships inside a plugin is invoked as `/<plugin>:<skill>`:

| Before | After |
|---|---|
| `/dotnet-reflect` | `/rlarch:dotnet-reflect` |
| `/dotnet-source` | `/rlarch:dotnet-source` |
| `/orchestrate` (hand-copied) | `/rlarch:orchestrate` |

Two things worth knowing:

- **The prefix only applies when you *type* a slash command.** Automatic invocation is unaffected —
  describe the task ("what's the exact signature of this NuGet method", "execute this plan end to
  end") and Claude picks the skill from its description without anyone typing a prefix. In day-to-day
  use you will rarely type the name at all.
- **There is no setting that turns the namespace off.** The installed binary exposes
  `extraKnownMarketplaces`, `enabledPlugins`, and `skillOverrides` — nothing that renames or
  un-prefixes a plugin skill. If you find a `skillDirectories` option suggested somewhere, it does not
  exist.

---

## If you hand-copied `orchestrate` into `~/.claude/skills/`

`orchestrate` was never installable, so the only way to have had it was to copy the folder into your
personal skills directory. **Delete that copy after installing the plugin.** Otherwise the skill loads
**twice** — once bare from `~/.claude/skills/orchestrate/`, once namespaced from the plugin — and you
pay for both copies in every session's context.

```powershell
# PowerShell
Remove-Item -Recurse -Force "$env:USERPROFILE\.claude\skills\orchestrate"
```

```bash
# bash / zsh
rm -rf ~/.claude/skills/orchestrate
```

Nothing is lost: the plugin copy is the same content, and it updates when the marketplace does.

---

## Optional: keep a bare `/orchestrate`

If you genuinely want to type `/orchestrate` rather than `/rlarch:orchestrate`, the only mechanism is
a skill folder sitting directly in `~/.claude/skills/<name>/`. That is a real trade, not a free win:

| You get | You give up |
|---|---|
| the bare `/orchestrate` | one-command install and update — you re-copy by hand on every change |
| | the marketplace as the source of truth for what version you're on |
| | **`dotnet-reflect` and `dotnet-source`** — see below |

And it is **either/or**. Mirroring a skill you *also* have installed as a plugin is exactly the
double-load described above: `orchestrate` is then loaded twice in every session — once bare, once
namespaced — and you pay for both copies of its description in every session's context, while edits
to one copy silently don't apply to the other.

So the only clean way to mirror is to uninstall the plugin — and **the plugin is indivisible**. There
is no way to install `rlarch` without `orchestrate`, or to remove just that one skill; uninstalling
takes `dotnet-reflect` and `dotnet-source` with it. The real choice is: all three skills, namespaced
and auto-updating — or a bare `/orchestrate` you maintain by hand, and no .NET navigation skills at
all.

```powershell
# PowerShell — from a clone of this repo
Copy-Item -Recurse -Force `
  ".\plugins\rlarch\skills\orchestrate" `
  "$env:USERPROFILE\.claude\skills\orchestrate"
```

```bash
# bash / zsh — from a clone of this repo
cp -R ./plugins/rlarch/skills/orchestrate ~/.claude/skills/orchestrate
```

The recommendation is: don't. Take the prefix, keep the updates.

---

## Troubleshooting

**A skill shows up twice in the skill list.**
You have both the plugin and a hand copy in `~/.claude/skills/`. Delete the hand copy (see above) and
restart.

**`claude plugin list` still shows `dotnet-reflect@rlarch` after updating the marketplace.**
Expected. Dropping a plugin from the marketplace manifest does not uninstall it — that's why step 1
comes first. Run the uninstall now, with the scope shown in `claude plugin list`.

**Uninstall reports nothing to remove.**
Wrong scope. `claude plugin uninstall` defaults to `--scope user`; pass `--scope project` (or `local`)
to match what `claude plugin list` reported.

**`/orchestrate` is not found.**
It's `/rlarch:orchestrate` now. Or just describe the job — "execute this plan end to end" — and let
Claude load it.

**`claude plugin details rlarch` shows fewer than three skills.**
The marketplace is stale. Run `claude plugin marketplace update rlarch`, then
`claude plugin install rlarch@rlarch` again, then restart.
