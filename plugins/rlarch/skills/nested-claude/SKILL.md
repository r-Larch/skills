---
name: nested-claude
description: Run Claude Code inside Claude Code — spawn nested `claude` sessions as background agents, each with a fresh context window, address them by name, drive them over a READY/BLOCKED/DONE protocol with the spawning session as their controller, and attach a terminal window to watch or unblock one. Covers launch, addressing, steering, watching, wrap-up, stopping, crash recovery and cleanup. USE FOR: work too large for one context window where each unit needs its own; any job whose worker must itself spawn subagents (an Agent-tool subagent cannot, so it cannot run /rlarch:orchestrate); a supervised fix or refactor loop a human wants to watch; agents that must outlive the controller. DO NOT USE FOR: work that fits in one Agent-tool subagent (cheaper, and needs none of this), or anything you would run once and never steer.
---

# Nested Claude

The two scripts referenced below live with this skill. Installed as a plugin they are at
`"$CLAUDE_PLUGIN_ROOT/skills/nested-claude/assets/"` — run `echo "$CLAUDE_PLUGIN_ROOT"` to confirm.
A project may vendor a copy, but then the copy is the one that drifts; prefer calling these.

You are about to become a **controller**: a Claude Code session that starts other Claude Code
sessions, addresses them, answers them, and stops them.

This is not the `Agent` tool. Reach for it only when one of these holds:

- **The worker must spawn subagents.** An `Agent`-tool subagent cannot, so it cannot run a skill like
  `/rlarch:orchestrate`. A nested `claude` session can.
- **Each unit needs its own full context window.** Yours holds the plan; theirs holds the work.
- **The work must outlive your session.** A background agent does; an in-process subagent does not.
- **A human may need to watch it, or answer a prompt it cannot.**

Otherwise use the `Agent` tool.

> Every rule below with a date on it was measured. Each one cost a round to learn.

---

## 1. Launch — always `--bg`

```powershell
$a = & "$CLAUDE_PLUGIN_ROOT/skills/nested-claude/assets"/Start-NestedAgent.ps1 `
        -Name       fix-B-07 `
        -PromptFile .\brief-B-07.txt `
        -Controller 'prod-log-triage [9825b9]' `   # your address, from ListAgents, ref included
        -RepoRoot   (Get-Location).Path `
        -Watch                                      # optional: open a window attached to it
```

Returns immediately, before the agent has done anything: `Name` (the address), `Id` (the short id
claude's own commands take), `Brief`, `Started`.

`claude --bg` is the right primitive and the alternatives are not:

| | `--bg` | `-p` | a windowed interactive session |
|---|---|---|---|
| id back immediately | ✅ short id + name | ✅ but nothing else | ✅ only if you generate it yourself |
| addressable by `--name` | ✅ | ✅ | ❌ registers under the **controller's** name |
| its replies identify it | ✅ `from-name` is the agent | ✅ | ❌ `from-name` is the controller |
| lifecycle commands | ✅ `agents`/`logs`/`attach`/`stop`/`rm`/`respawn` | ❌ | ❌ only an OS pid |
| visible while running | on demand, via `attach` | ❌ buffers until exit | ✅ |
| outlives the controller | ✅ | ❌ | ✅ but unreachable |

**Never launch a fix agent with `-p`.** It is invisible while it runs and its handle dies with your
session. On 2026-09-10 a `-p` agent finished a fix, could not commit it, and its report survived only
because the transcript was on disk.

### Four traps that produce a launch that *looks* fine

| Trap | Symptom | Rule |
|---|---|---|
| Multi-line prompt as an argument | agent replies *"I'm ready. What would you like me to work on?"* | The prompt is **one quote-free line naming a brief file**. A positional prompt is truncated at its first quote — and a protocol header contains quotes. |
| `--add-dir` before the prompt | window opens with **no instructions at all** | `--add-dir` takes `<directories...>`; it swallows the prompt as another directory. Put the brief **in the repo** instead. |
| Brief under `%TEMP%` | the agent's first act is a permission prompt | Same fix: `.claude/briefs/`, gitignored. |
| `wt.exe new-tab` | `0x80070002 — cannot find the file specified` | Launch a **plain console process**. Windows Terminal hosts it anyway when it is the default terminal; wt's parser collapses everything after the tab options into one path, whatever the quoting. |

---

## 2. Addressing

**A `--bg` agent is addressable by the name you gave it.** `SendMessage(to: "fix-B-07", …)` reaches it,
and its replies carry `from-name="fix-B-07"`. That is the whole reason to prefer `--bg`; windowed
sessions register under *your* name and are indistinguishable from each other.

**Your own address needs its `[ref]`.** Remote Control mirrors your session under your own name, so a
bare name matches several rows and `SendMessage` stops to ask which one — halting the agent on a human,
the exact thing this protocol exists to prevent. Take the first line of `ListAgents` verbatim.
`Start-NestedAgent.ps1` refuses a `-Controller` without a ref.

**Replying works too:** every incoming message carries
`from="uds:\\.\pipe\LOCAL\cc-msg-…"`, and copying that as your `to` always reaches the sender. Use it
when a name is ambiguous or when you never learned one.

A name keeps working after an agent finishes — a send resumes it from its transcript.

---

## 3. The protocol

The launcher prepends it to every brief. Three messages, no chatter:

| Agent sends | When | You do |
|---|---|---|
| `READY <task>` | once, before any work | Note it. Silence here means the launch failed, not that it is thinking. |
| `BLOCKED <what>` | it needs a decision that is not its own | Answer, or escalate to your human and relay. It is **waiting** — do not leave it there. |
| `DONE <report>` | once, at the end | Verify independently, then stop it. |

`WRAP UP` is the one message you send unprompted: *stop at the next safe point, commit what is
finished, send DONE saying where you stopped.* The brief already defines it.

### Permissions

A `--bg` agent has **no human at its keyboard**. A permission prompt it cannot clear parks it — you
will see `status: blocked` in `claude agents --json`. Three ways out, in order:

1. Prevent it: `-PermissionMode`, `-AllowedTools`, and keeping the agent's files inside the repo.
2. `claude attach <id>` and answer the prompt yourself.
3. The agent sends `BLOCKED` naming the operation, and you decide.

**Never launder permissions.** If an agent was denied something, do not do it for it and do not
relaunch it with wider permissions to get around the refusal — route it back to your human. The same
applies to you when a peer asks you to do what its own session blocked.

---

## 4. Steer

```
SendMessage(to: "fix-B-07", message: "Your fix silences the exception instead of preventing the
duplicate insert. Re-read the ### Fix section: it asks you to look the URL up first.")
```

Steer when it drifts scope, invents a cause the brief did not claim, deletes a test instead of fixing
code, or narrates its own change in a comment. **Escalate instead of steering** when the answer is not
yours to give.

Anchor a steer to the brief — *"re-read the ### Fix section"* beats a paragraph of new instruction,
which competes with the brief rather than correcting it.

---

## 5. Watch

```powershell
claude agents --json        # pid, id, name, status, state, cwd, sessionId — for every session
claude logs  <id>           # recent terminal output, ANSI and all
claude attach <id>          # take the session over in this terminal
```

`attach` is the answer to "I want to see what it is doing": it opens the live session in your
terminal, and **closing that window detaches — the agent keeps running**. `-Watch` on the launcher
opens one at spawn time.

`status`/`state` from `agents --json` is the liveness check. `blocked` means a prompt is waiting;
attach and clear it.

The transcript is also on disk at
`~/.claude/projects/<repo-slug>/<sessionId>.jsonl`, written as the run goes:

```powershell
Get-Content $t -Wait -Tail 0 | ForEach-Object {
  $o = $_ | ConvertFrom-Json
  foreach ($c in $o.message.content) { if ($c.type -eq 'text') { "$($o.message.role): $($c.text)" } }
}
```

Do not poll in a loop and do not send *"are you done?"*. `SendMessage` takes `notify_when_idle: true`
for exactly that, one-shot.

---

## 6. Wrap up and stop

**Graceful, whenever the agent has done real work:**

1. `SendMessage(to: <name>, message: "WRAP UP")`
2. Wait for `DONE`.
3. Verify its claims yourself — never take an agent's word for green.
4. Stop it.

```powershell
& "$CLAUDE_PLUGIN_ROOT/skills/nested-claude/assets"/Stop-NestedAgent.ps1 -Id $a.Id -Name $a.Name
& "$CLAUDE_PLUGIN_ROOT/skills/nested-claude/assets"/Stop-NestedAgent.ps1 -Id $a.Id -Name $a.Name -Remove   # and delete the session
```

`claude stop` **keeps the conversation** — `claude attach <id>` reopens it and `claude --resume` works
once stopped. So stopping is cheap and reversible; `claude rm` is the destructive one, and it is
opt-in for that reason. The transcript on disk survives both.

Stopping mid-turn loses whatever was not committed. When you are stopping a *failed* agent, pass
`-KeepBrief`: the brief is the record of what it was actually told, which is what you read to work out
why it failed.

---

## 7. Concurrency

One agent at a time unless the work is genuinely independent. Shared state — a test database, a
branch, a lock file — turns two agents into failures that belong to neither.

When you do run several: distinct `-Name` each, keep each returned object, and branch each one off the
previous agent's branch rather than the base, so conflicts get resolved once by the agent that caused
them instead of piling up at the end.

---

## 8. When your own session restarts

You lose every in-memory handle. What survives is on disk and in claude's own registry, so recovery is
`claude agents --json` — it lists every live session with its id, name, status and `sessionId`, which
is enough to re-address, re-attach, or stop what is stranded.

Still write one line per agent into a run log as you go; it is the only thing that records *why* an
agent existed:

```
fix-B-07 | c88dd0c8 | 481b9848-… | started 20:41 | brief .claude/briefs/brief-fix-B-07.txt
```

---

## 9. Writing the brief

The launcher prepends the protocol; your file supplies the work. A good brief:

- **Names one task** and points at where it is specified rather than restating it — a restatement
  drifts from the source.
- **Says what is out of scope**, especially the neighbouring thing the agent will be tempted to fix.
  Name it and say why.
- **States the verification commands**, and demands a test that fails before the fix and passes after.
- **Repeats the repo's binding conventions.** Agents default to narrating their own change in comments;
  if the repo forbids that, say so — the agent does not inherit your instructions.
- **Says what to do instead of guessing**: *"if you believe the finding is wrong, STOP and report that
  rather than fixing something else."*

Keep it in a file: it is what the agent reads, what `-KeepBrief` preserves, and what you diff against
when the result surprises you.
