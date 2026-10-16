---
name: nested-claude
description: Run Claude Code inside Claude Code — spawn nested `claude` sessions as background agents, each with a fresh context window, address them by name, drive them over a READY/BLOCKED/DONE protocol with the spawning session as their controller, arm a watchdog that wakes you when one is blocked or stalled instead of polling it, and attach a terminal window to watch or unblock one. Covers launch, the permission profiles an unattended agent needs, addressing, steering, watching, wrap-up, stopping, crash recovery and cleanup. USE FOR: work too large for one context window where each unit needs its own; any job whose worker must itself spawn subagents (an Agent-tool subagent cannot, so it cannot run /rlarch:orchestrate); a supervised fix or refactor loop a human wants to watch; agents that must outlive the controller. DO NOT USE FOR: work that fits in one Agent-tool subagent (cheaper, and needs none of this), or anything you would run once and never steer.
---

# Nested Claude

The three scripts referenced below live with this skill. Installed as a plugin they are at
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
        -RepoRoot   ../repo.worktrees/b-07 `        # its OWN worktree - see §7, not optional for --bg
        -Toolset    fix `                           # what it may do unattended - see §3
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

### Traps that produce a launch that *looks* fine

| Trap | Symptom | Rule |
|---|---|---|
| Multi-line prompt as an argument | agent replies *"I'm ready. What would you like me to work on?"* | The prompt is **one quote-free line naming a brief file**. A positional prompt is truncated at its first quote — and a protocol header contains quotes. |
| `--add-dir` before the prompt | window opens with **no instructions at all** | `--add-dir` takes `<directories...>`; it swallows the prompt as another directory. Keep the brief **in the repo**, and pass extra dirs via `-AddDir`, which the launcher places where another flag closes it. |
| `--allowedTools` before the prompt | same — **no instructions at all** | Same variadic shape, and the launcher hit it too. Any variadic flag needs **another flag after it** to close the list; the launcher now puts `--permission-mode` there. |
| Brief under `%TEMP%` | the agent's first act is a permission prompt | Same fix: `.claude/briefs/`, gitignored. |
| `wt.exe new-tab` | `0x80070002 — cannot find the file specified` | Launch a **plain console process**. Windows Terminal hosts it anyway when it is the default terminal; wt's parser collapses everything after the tab options into one path, whatever the quoting. |
| `-RepoRoot` that is not a working tree | agent starts in an empty folder and reports **READY** | A failed `git worktree add` upstream does not stop the launch: the brief directory is created with `-Force` and the agent comes up with nothing but its brief. The launcher now refuses; if you are not using it, check the tree exists before spawning. **A failed setup step must not produce a confident agent.** |

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

**A `notify_when_idle` subscription does not follow the name.** It is bound to the session that held
the name when you subscribed, so after a stop-and-relaunch the notice arrives for the *dead* one:
*"fix-B-02 has exited before going idle"*, while the live `fix-B-02` is busy working. Read the time
before you believe it. Either re-subscribe after every relaunch, or give each attempt a distinct name
(`fix-B-02-a`, `-b`) so a stale notice cannot be mistaken for the live agent dying.

---

## 3. The protocol

The launcher prepends it to every brief. Three messages, no chatter:

| Agent sends | When | You do |
|---|---|---|
| `READY <task>` | once, before any work | **Read what it says, not just that it arrived.** It is your only proof the prompt survived: a `READY` naming the right finding and the right branch means the brief landed; a generic *"I'm ready, what should I work on?"* means a variadic flag ate it. Silence means the launch failed, not that it is thinking. |
| `BLOCKED <what>` | it needs a decision that is not its own | Answer, or escalate to your human and relay. It is **waiting** — do not leave it there. |
| `DONE <report>` | once, at the end | Verify independently, then stop it. |

`WRAP UP` is the one message you send unprompted: *stop at the next safe point, commit what is
finished, send DONE saying where you stopped.* The brief already defines it.

### Permissions — the most common way a launch quietly fails

The launcher's `-PermissionMode` defaults to `auto` (see `/rlarch:controller` §2). The rest of this
section is what happens under any other mode. A `--bg` agent has **no human at its keyboard**, and
`--permission-mode acceptEdits` only covers *edits*. The first shell command it has not been granted parks it, and from the outside a parked
agent is indistinguishable from a busy one. On 2026-09-10 an agent stopped nine minutes in on
`ls /p/packages/nuget/npgsql/` — exploration, not a dangerous act — and would have sat there forever.

The tell is in `claude agents --json`:

```json
{ "id": "e62a5e14", "status": "waiting", "waitingFor": "permission prompt", "state": "blocked" }
```

`waitingFor` is the field to key on. §5's watchdog exists because nothing else surfaces it.

**Grant the surface up front.** `-Toolset` is a named profile, merged with anything you add in
`-AllowedTools`:

| `-Toolset` | Grants | Use for |
|---|---|---|
| `readonly` | `Read`/`Glob`/`Grep`, and the read-only shell an agent reaches for without thinking — `ls cat head tail grep rg find wc sed -n echo which`, plus `git status/diff/log/show/branch/rev-parse` | recon, review, an agent that must not change anything |
| `fix` *(default)* | all of the above plus `Write`/`Edit`, `git add`/`git commit`, `dotnet build`/`dotnet test`/`pnpm run` | an agent that edits, tests and commits |
| `none` | nothing beyond `-PermissionMode` | an agent that genuinely never shells out |

**`git push` is deliberately absent from every profile.** An allow-list is the only thing between a
nested agent and a remote its controller was told never to touch. Do not add it.

**A package manager is a runner too.** `pnpm run <script>` executes whatever `package.json` says, and
Gaia's `all` script starts `dotnet run` — production. Grant the exact script an agent needs
(`pnpm --dir Gaia.Client run ts:*`), never `pnpm:*`. Note that the `--dir` it needs to avoid `cd` is
itself a flag before the subcommand, so it has to be spelled out in the pattern.

**Nor is `dotnet tool run`, and that one is easy to miss.** A repo's `.config/dotnet-tools.json` is a
list of commands the agent can reach by another name — Gaia's holds `dotnet-ef`, so granting
`dotnet tool` would hand back the production database you just forbade. Before you widen a profile
for a *runner* (`dotnet tool`, `npx`, `uvx`, `pnpm exec`), read what it can run.

A narrower list is not safer, it is just later: the agent parks instead of being refused, and you
find out when nothing has happened for an hour.

**On Windows, cover both shells.** `Bash(...)` patterns do not match the `PowerShell` tool, and an
agent reaches for whichever fits the thought. The profiles above list both.

**An allow-list grants tools, not paths.** `Bash(ls:*)` is granted and the agent still parks on
`ls /p/packages/nuget`, because the target is outside `-RepoRoot`. Pass the directories it will
legitimately need with `-AddDir` — a package cache is the usual one:

```powershell
-Toolset fix -AddDir @("P:\packages\nuget", "$env:USERPROFILE\.nuget\packages")
```

This is the same `--add-dir` that eats the prompt when it is last. The launcher puts it between
`--allowedTools` and `--permission-mode`, so each variadic flag is closed by the next one and the
prompt sits safely after a flag that takes exactly one value. **Do not reorder those three.**

**An allow-list matches a command PREFIX, not a pipeline.** `PowerShell(dotnet test:*)` is granted and
`dotnet test ... | Select-String ... | Select-Object -First 30` still parks: it is not that shape. Same
for `2>&1`, `| head`, `&&`, `;` and `cd x && y`. Put the rule in the brief, because you cannot
enumerate the shapes:

> Run bare commands only — no pipes, no redirection, no `&&`, no `;`, no `cd`, and no flags before a
> subcommand (`git status`, never `git -C <path> status`). If the output is too long, narrow the
> command's own filter. To search a file, use `Grep`, not a shell pipe.

**Give the alternative, not only the prohibition.** An agent reaches for a pipe when output is long,
so the brief has to say what to do instead — narrow with the *tool's* own argument
(`--filter`, `--max-count`, `-n`), never the shell's. A brief that forbids pipes without offering the
filter form gets ignored at exactly the moment it matters.

**And say which tool replaces the shell.** An agent reaches for `git ls-files` or `find` to locate
files because nothing told it there is a `Glob`. A prohibition on shell shapes without naming
`Glob`/`Grep`/`Read` as the route leaves it improvising with the thing it has already been refused.

The general form, which an agent can apply to shapes you did not foresee:

> Run the simplest command that does the job, from where you already are. If you are telling a tool
> where to look, you are probably standing in the wrong place.

**A command too long to parse cannot be allow-listed either.** Over ~1015 bytes the parser gives up
with *"Command too long for parsing"* and prompts regardless of content — which a `git commit -m` with
a real commit message reaches easily, and a heredoc or PowerShell here-string reaches faster. So:

> Commit with `git commit -F <file>` — write the message with `Write` first, **to a path inside your
> worktree**, and delete it afterwards. Never an inline `-m` with a long body, no heredocs, no
> here-strings.

The "inside your worktree" half is not fussiness: `Write` is granted as a *tool*, and the path rule
is separate, so a scratch file anywhere else parks the agent at the very last step — after the work
is done and before it is committed, which is the most expensive moment to lose it.

And add the critical path, because most parks are the agent exploring rather than working:

> Your critical path is: read the finding, write the fix, write the test, red-check, build, test,
> commit. Do not probe the environment for tooling you were not told to use.

An agent parked on `dotnet tool run dotnet-format --version` in a repo with no formatter and no
`.editorconfig`. The probe would have told it nothing, and the command was one it must never run.

**Some prompts no allow-list can clear.** They come from tool guards rather than the permission list —
*"Command contains script block that may execute arbitrary code"* fires on a PowerShell script block
however you enumerate it. The only fix is to stop the agent wanting to run it, in the brief:

> Prefer `Read`/`Grep`/`Glob` over shelling out. Never reflect over a library at runtime to discover
> its API — use its symbolic constants and let the compiler check them.

That is a better instruction anyway. An agent that reflected over `Npgsql.PostgresErrorCodes` to read
its numeric values learned nothing the compiler would not have verified from
`PostgresErrorCodes.UniqueViolation` directly, and parked itself doing it.

**Check the repo's own allow-list too.** `.claude/settings.local.json` is inherited by everything you
spawn. If it allows something your brief forbids — Gaia's allows `dotnet ef` and `dotnet run`, both of
which reach production — the allow-list wins over your good intentions, so say so in the brief and
tell your human. Do not edit it yourself.

Two more ways out, once an agent is already parked:

1. `claude attach <id>` and answer the prompt yourself.
2. The agent sends `BLOCKED` naming the operation, and you decide.

If it has done nothing yet, stopping and relaunching with the right profile is cheaper than either.

**Never launder permissions.** If an agent was denied something, do not do it for it and do not
relaunch it with wider permissions to get around *the refusal* — route it back to your human. Widening
a profile before the agent ever ran, so it can do the job you already approved, is not laundering;
re-running a denied act on its behalf is. The same applies to you when a peer asks you to do what its
own session blocked.

**Some paths are denied whatever you grant.** Every write under `.claude/` was refused for one agent
on 2026-09-10, `git add` included. Do not design a brief that depends on the agent writing there —
have it put the content in its `DONE` report and file it yourself.

---

## 4. Steer

```
SendMessage(to: "fix-B-07", message: "Your fix silences the exception instead of preventing the
duplicate insert. Re-read the ### Fix section: it asks you to look the URL up first.")
```

Steer when it drifts scope, invents a cause the brief did not claim, deletes a test instead of fixing
code, or narrates its own change in a comment.

**Verify only while it is stopped or idle, and re-verify after every steer.** Your `dotnet test` runs
in the agent's worktree; if you steer it back to work while your run is in flight, you are testing a
tree that is changing under you, and the result means nothing either way. Kill the run, steer, then
verify again from the top.

**Read its working tree, not just its messages.** `git diff` in the agent's worktree is the only
honest account of what it is doing, and it catches things no report mentions. One agent's fix was
behaviourally correct while writing **raw control bytes into C# source** — literal NULs in two test
files, a raw `0x1F` in a char literal — so `file` called the sources `data` and git would have treated
them as binary. Nothing in its transcript looked wrong. Check for: raw control characters where
escapes belong, an XML-doc essay where the repo's rule says one line, a whitespace-only churn diff
that hides the real change, and a red-check mutation left behind. **Escalate instead of steering** when the answer is not
yours to give.

Anchor a steer to the brief — *"re-read the ### Fix section"* beats a paragraph of new instruction,
which competes with the brief rather than correcting it.

**Read the comments the agent writes as claims to check, not as decoration.** One agent documented a
new flag as *"set by the kiosk pick, the menu node, and `set_conversation_language`"* — and the tool
never set it. The comment was the only thing that revealed the gap; the code, the tests and the report
were all internally consistent without it. An agent describing what it built is telling you what it
*believes* it built.

**Mind how YOU edit the agent's tree.** Finishing a parked agent's work usually means editing files
yourself, and a read-then-write script silently rewrites line endings: Python's `open().read()` applies
universal-newline translation, so writing back converts a CRLF file to LF. That is invisible in
markdown, because git normalises on commit — and **not** invisible in any language where a
raw/verbatim string literal carries its file's endings into runtime output: C# `"""`, Go backticks,
JS template literals, Python triple quotes. On 2026-09-11 that changed the text of a prompt sent to a
model, in the working tree only, and was caught solely because the repo had a byte-for-byte snapshot
test whose expected value was **transcribed literally rather than captured** — its author had written
that a captured snapshot *"would re-bless whatever the builder happens to emit"*, and they were right.
Prefer in-place edits; if you must script one, check the endings afterwards.

**Your steer can be wrong, and a wrong steer gets baked into a commit message.** A steer once asserted
that a builder ran "per turn" — true on two transports, false on the one under discussion — and the
agent would have repeated it as justification. Verify a claim before you send it; if you find out
afterwards, send the correction *before* it commits, and say plainly which part was wrong. An agent
will take your word over the code, which is exactly why your word has to be checked.

### When the agent cannot run the gate

Sometimes the check that matters is one the agent has no way to execute — a frontend typecheck with no
`node_modules`, an integration suite needing a service, a build only the controller can do. The wrong
answers are to skip it, or to let the agent hand you an unverified leg and call it done.

**Make it predict the gate's output, precisely enough to be wrong.** Then run the gate and compare.
The brief says: *"you will not run the typecheck — the controller does, after the merge. Your report
must state exactly what you expect it to say."* A prediction that matches is evidence; one that does
not is a finding. Either way the agent has had to think about the whole surface rather than the line
it changed.

**Where no harness exists and building one is scope creep, demand an enumeration instead.** For a
five-line error handler, "write a test" means building a rig around a hook that has none. What is
actually wanted is: *what does this render for every shape the input can take?* A table of
`err.error.message` / `err.message` / only `err.type` / none of them / a circular object, checked
against the diff, catches more than a test of the happy path would — and it surfaced two edges the
author had not stated (an empty-string field does not fall through `??`; an `Error` with no message
stringifies to `{}`).

**When nothing can catch a wrong guess, redundancy is the gate — and the agent should say so.** One
agent could not confirm an SDK's field names by reading, and noted that a typecheck could not have
caught a wrong name either, because the objects were `any`. So it recorded the value it needed from
two independent points, each guarded, so either alone sufficed and a wrong name degraded to the old
behaviour rather than throwing. That is the right shape for an unverifiable assumption: make being
wrong survivable, and declare it.

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
for exactly that, one-shot — omit `message` and it is a pure subscription that costs the agent nothing.

### Waking yourself up: the watchdog

`notify_when_idle` answers *"tell me when it finishes"*. It does not answer the two questions that
actually cost you an hour: **is it stuck?** and **is it still making progress?** A parked agent never
goes idle, so the subscription never fires, and `status: waiting` is only visible if you look.

Arm a watchdog instead. It runs in the background and speaks only when something has happened:

%spowershell
Monitor(command: 'python -u "$CLAUDE_PLUGIN_ROOT/skills/nested-claude/assets/watch-nested-agent.py" cffcbd64 --beat 12 --stall 8',
        persistent: true, description: 'fix-B-02 watchdog')
%s

| It emits | When | What you do |
|---|---|---|
| `BLOCKED` | `waitingFor` appears — a permission prompt with nobody to answer it | `claude attach <id>`, or stop and relaunch with the right `-Toolset` |
| `STALL` | the transcript stopped growing for `--stall` minutes while the agent is still listed | look at what it last said; a hung tool call or a turn that died |
| `STATUS` back to `busy` | it cleared its own prompt | you may steer again — messages only drain while it runs |
| `STATUS` | `busy -> idle`, `idle -> done` | the turn ended; go verify |
| `GONE` | it left `claude agents --json` | it exited or was stopped; the transcript on disk outlives it |
| `HEARTBEAT` | every `--beat` minutes | one line: elapsed, status, the tail of its last message. `--beat 0` switches it off |

Each fires **once per occurrence**, not once per poll, so a long run stays quiet. Set `--beat` to a
value you would actually tolerate being woken at: 10–15 minutes for a fix that should take under an
hour, higher for an overnight run.

The design rule underneath it, which is worth carrying to any watch you write: **silence is not
success.** A watch that only looks for the finish line stays silent through a crash, a park and a
hang — all three look exactly like "still working". Cover the failures first and the success last.

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

## 7. Concurrency, and the tree you are both standing in

One agent at a time unless the work is genuinely independent. Shared state — a test database, a
branch, a lock file — turns two agents into failures that belong to neither.

### A `--bg` agent cannot edit your checkout at all

This is not a preference, it is a guard. Every `Edit` a background session attempts in the shared
working tree is refused:

> This background session hasn't isolated its changes yet. Call EnterWorktree first so edits land in a
> worktree instead of the shared checkout, then retry this edit using the worktree path.

So **plan for a worktree from the start**. Two ways, and the first is better:

1. **Make it yourself, before the agent needs it.** You keep your checkout, the branch name is yours,
   and the merge afterwards is ordinary:
   ```bash
   git switch <collector>                                   # release the branch first
   git worktree add ../repo.worktrees/<slug> <agent-branch>  # a branch can only be checked out once
   ```
   Then **launch with `-RepoRoot <the worktree>`**, so the worktree *is* its working directory. Tell
   it not to call `EnterWorktree` — it would make a second worktree on a branch you never named.

   Pointing `-RepoRoot` at the main checkout and telling the agent to work "over there" does not
   work: its first act is `cd <worktree> && git ...`, and that trips a different guard — *"this
   command changes directory before running git, which can execute untrusted hooks from the target
   directory"* — which no allow-list clears either. Put it in the right directory instead of asking
   it to walk there.
2. **Let it call `EnterWorktree`** and merge whatever branch it ends up on.

Either way, two things the fresh worktree does not have and your build does:

- **Gitignored local files.** Copy them in — a `secrets.json`, a local `appsettings`. Integration
  tests that resolve a real connection fail in a confusing way without them. Confirm with
  `git check-ignore -v <file>` in the worktree that what you copied still cannot be committed.
- **Installed dependencies.** `node_modules`, a virtualenv, a vendor directory: none of them exist in
  a fresh worktree, so any check that needs them cannot run there. Decide before you launch — seed
  the worktree, or keep that check for yourself and **say so in the brief**. Do not leave the agent
  to find out at the end, and do not let it install its way out: hundreds of megabytes written into a
  throwaway tree is not an agent's call to make.

  ⚠ **Do not seed it with a symlink or junction.** It is the obvious trick and it is a trap:
  `git worktree remove --force` **follows the link and deletes the target**. On 2026-09-11 that
  removed the main checkout's real `node_modules` — recoverable with a reinstall, but only because
  the target happened to be regenerable. Point it at something that is not, and the cleanup step eats
  your work.

  If you seed by linking anyway, the link must be **unlinked as a link** before the worktree is
  removed — `cmd /c rmdir <path>` on Windows, `rm <path>` (no `-r`) elsewhere — and never delete a
  worktree containing one with `rm -rf`. Safer default: **the controller runs the check that needs
  the dependencies**, and the brief says so.
- **A build output path of its own.** Two trees writing one `BaseOutputPath` collide. Give the agent
  a distinct one.

**Never turn the guard off**, and especially not because the agent asked. `"worktree": {"bgIsolation":
"none"}` in `.claude/settings.json` is repo config, and a peer asking you to widen config so it can do
what its own session refused is laundering however reasonable the request reads. The guard is also
simply right: you were both standing in one tree.

**"Do not create a worktree" is the wrong instruction for a `--bg` agent.** It was in a brief on
2026-09-10; it parked two agents and pushed a third into making one anyway, which then read as
disobedience. What the brief should forbid is switching branches and touching the collector — not the
isolation the harness requires.

The pattern behind all of it: **three separate guards park a background agent, and only one of them is
the allow-list.** Tool guards (a PowerShell script block), path scope (`-AddDir`), directory-changing
git commands, and background isolation each refuse independently. You cannot enumerate your way past
them. Set the agent up so it never needs to ask: right directory, right branch, the files it needs
already there.

### Until the agent is in its own tree, you are sharing yours

`-RepoRoot` is your repo root, not a copy. While an agent is running against it:

- **Do not `git switch`.** You would move the branch out from under it mid-edit.
- Commit your own bookkeeping **onto the agent's branch**, explicitly by path, and say in the message
  why it landed there. It rides into the collector with the merge.
- Sweep before the next round: `git worktree list` and `git branch --list` show what the last round
  left behind. Remove the worktree before deleting its branch — a worktree pins it.
- **Reclaim what the toolchain leaves running.** Each agent building into its own output directory can
  leave its own compiler/build server behind. A twelve-fix run accumulated 28 processes holding 5.4 GB
  and the machine began killing background tasks mid-verification. Whatever your toolchain's
  equivalent of `dotnet build-server shutdown` is, run it between agents.

When you do run several: distinct `-Name` each, keep each returned object, and branch each one off the
previous agent's branch rather than the base, so conflicts get resolved once by the agent that caused
them instead of piling up at the end.

---

## 8. When your own session restarts

You lose every in-memory handle. What survives is on disk and in claude's own registry, so recovery is
`claude agents --json` — it lists every live session with its id, name, status and `sessionId`, which
is enough to re-address, re-attach, or stop what is stranded.

**An agent that is no longer listed is still readable.** Its transcript at
`~/.claude/projects/<repo-slug>/<sessionId>.jsonl` survives `stop` and `rm` alike, and its **last
assistant message is the `DONE` report** — the full one, including whatever it could not do. That is
how a finished agent's result was recovered after its handle was gone:

```python
rows = [json.loads(l) for l in open(PATH, encoding='utf-8') if l.strip()]
say  = lambda m: ' '.join(x.get('text','') for x in m.get('content') or []
                          if isinstance(x, dict) and x.get('type') == 'text')
print(next(say(o['message']) for o in reversed(rows)
           if (o.get('message') or {}).get('role') == 'assistant' and say(o['message']).strip()))
```

So a `--resume <sessionId>` your human hands you is not a session to re-enter — it is a file to read.

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
  if the repo forbids that, say so — the agent does not inherit your instructions. When the work is
  tracked in WorkState, the run's binding rules are its constraints, and `work_brief(scope: "<item
  ref>", for: "worker")` prints them in its BINDING block — paste that rather than retyping them.
- **Says what to do instead of guessing**: *"if you believe the finding is wrong, STOP and report that
  rather than fixing something else."*
- **Carries the baseline you already measured**, as numbers. Not *"take a baseline"* — the exact
  failing test name and the exact counts, plus *"do not fix it, do not report it as a regression, and
  do not let a subagent clean it up"*. An agent handed a red suite it did not cause will either claim
  the regression or repair it.
- **Names what is deliberately out of scope, and says it is your decision, not an oversight.** An
  agent that reads a finding listing four fixes and is briefed on two will otherwise do all four, or
  stop to ask. *"Deferred by controller decision, because …"* answers it in advance.
- **Forbids what you are relying on it not doing**, in words: no worktree, no branch switch, no
  `git add -A`, no touching the collector. Implication is not instruction.
- **Routes artifacts it may not be able to write.** If a path might be denied — `.claude/` was — ask
  for the content in the `DONE` report and file it yourself, rather than discovering at the end that
  the record does not exist.

- **Names the environment's noise.** An agent parks wherever the environment says something it cannot
  interpret and the brief is silent. One parked investigating `LF will be replaced by CRLF` warnings
  that appear on every commit in that repo and mean nothing. If your repo emits a benign signal
  routinely, say so and say what to do instead of looking into it.

### The rule underneath all of these

**Every rule you add to a brief is a potential park, because a rule that demands a capability the
agent does not have is a trap you set yourself.**

That is not a reason to write fewer rules. It is a reason to write each one as a *route* rather than a
*prohibition*:

| Prohibition that parked an agent | The rule that works |
|---|---|
| "no pipes" | "no pipes — narrow with the tool's own `--filter`" |
| "commit with `-F <file>`" | "…to a path **inside your worktree**" |
| "delete the scratch file afterwards" | "leave it; the controller removes it" — deletion was never granted |
| "do not change that warning count" | "the controller checks the count" — it had no way to read one |
| "do NOT create a worktree" | exactly backwards: a `--bg` agent **must** have one |

Each of those was added to prevent a real problem and caused the next one. After eleven parks in one
round and zero in the next, the difference was not the harness — it was that every rule finally
carried its sanctioned route.

Keep it in a file: it is what the agent reads, what `-KeepBrief` preserves, and what you diff against
when the result surprises you.
