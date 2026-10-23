#!/usr/bin/env bash
# start-nested-agent.sh — start a nested Claude Code agent as a background session (macOS / Linux).
# The Windows twin is Start-NestedAgent.ps1; the two must stay behaviourally identical, the protocol
# header text above all. See that file for the rationale behind each check.
#
#   start-nested-agent.sh --name fix-B-07 --prompt-file ./brief.txt --controller 'my-session [9825b9]' --watch
#
# Prints Name=, Id=, Brief=, Started= lines. --dry-run does everything except run claude: it
# validates, writes the brief, and prints the exact argv.
#
# Written for bash 3.2 (macOS's /bin/bash): no ${x,,}, no mapfile, no associative arrays, and every
# possibly-empty array expanded as ${a[@]+"${a[@]}"} because set -u rejects "${a[@]}" there.
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
usage: start-nested-agent.sh --name <name> --prompt-file <file> --controller '<name> [<ref>]'
                             [--repo-root <dir>] [--permission-mode <mode>]
                             [--toolset none|readonly|fix] [--allowed-tools <pattern>]...
                             [--add-dir <dir>]... [--watch] [--dry-run]
EOF
  exit 2
}
die() { echo "start-nested-agent: $*" >&2; exit 1; }

name="" prompt_file="" controller=""
repo_root="$(pwd)"
permission_mode="auto"
toolset="fix"
allowed_tools=()
add_dir=()
watch=0
dry_run=0

while [ $# -gt 0 ]; do
  case "$1" in
    --name)            [ $# -ge 2 ] || usage; name="$2"; shift 2 ;;
    --prompt-file)     [ $# -ge 2 ] || usage; prompt_file="$2"; shift 2 ;;
    --controller)      [ $# -ge 2 ] || usage; controller="$2"; shift 2 ;;
    --repo-root)       [ $# -ge 2 ] || usage; repo_root="$2"; shift 2 ;;
    --permission-mode) [ $# -ge 2 ] || usage; permission_mode="$2"; shift 2 ;;
    --toolset)         [ $# -ge 2 ] || usage; toolset="$(printf '%s' "$2" | tr '[:upper:]' '[:lower:]')"; shift 2 ;;
    --allowed-tools)   [ $# -ge 2 ] || usage; allowed_tools+=("$2"); shift 2 ;;
    --add-dir)         [ $# -ge 2 ] || usage; add_dir+=("$2"); shift 2 ;;
    --watch)           watch=1; shift ;;
    --dry-run)         dry_run=1; shift ;;
    -h|--help)         usage ;;
    *)                 echo "start-nested-agent: unknown argument: $1" >&2; usage ;;
  esac
done

[ -n "$name" ]        || { echo "start-nested-agent: --name is required" >&2; usage; }
[ -n "$prompt_file" ] || { echo "start-nested-agent: --prompt-file is required" >&2; usage; }
[ -n "$controller" ]  || { echo "start-nested-agent: --controller is required" >&2; usage; }
case "$toolset" in none|readonly|fix) ;; *) die "--toolset must be none, readonly or fix (got: $toolset)" ;; esac

command -v claude >/dev/null 2>&1 || die "claude is not on PATH."
[ -e "$prompt_file" ] || die "Prompt file not found: $prompt_file"

# --repo-root must already be a working tree: a failed `git worktree add` upstream must fail here,
# loudly, not launch a confident agent into a folder holding nothing but its own brief.
[ -e "$repo_root" ]      || die "--repo-root does not exist: $repo_root. Did git worktree add fail?"
[ -e "$repo_root/.git" ] || die "--repo-root is not a git working tree: $repo_root. A --bg agent must be launched inside one - see SKILL.md section 7."
# Case-insensitive hex, as PowerShell's -notmatch is.
if ! [[ "$controller" =~ ^[^[:space:]].*[[:space:]]\[[0-9a-fA-F]{6}\]$ ]]; then
  die "--controller must include the ref, e.g. 'my-session [9825b9]'. Run ListAgents and copy the first line."
fi

# Identical to the here-string in Start-NestedAgent.ps1. Change one, change both.
header="Your controller is the Claude Code session '$controller'. It started you, it is watching you, and it
is who you report to and ask. If a human attaches to this session they are observing, not directing:
questions go to the controller. SendMessage with to=\"$controller\", verbatim, brackets included.

Send exactly these, and no progress chatter - the controller reads your transcript for that:

  READY <task>   once, immediately, before any other work.
  BLOCKED <what> the moment you need a decision that is not yours - the task looks wrong, it needs a
                 schema change or a production write, the scope is ambiguous. Then STOP and wait for
                 the reply. Do not guess and carry on.
  DONE <report>  once, at the end: what you changed with file:line, the test that covers it, the
                 exact build and test result, and anything you could not do.

Open every message with your name '$name'.

If the controller sends you WRAP UP: stop at the next safe point, commit what is finished, and send
DONE saying exactly where you stopped. Do not start anything new.

You have no human at a keyboard. If you hit a permission prompt you cannot clear, do not work around
it and do not ask a peer to act for you - send BLOCKED naming the exact operation, and wait.
"

# Inside the repo on purpose: a brief under $TMPDIR makes the agent's first Read raise a permission
# prompt, and --add-dir cannot buy its way out (it is variadic and would swallow the prompt).
repo_root="${repo_root%/}"
brief_dir="$repo_root/.claude/briefs"
mkdir -p "$brief_dir"
brief_path="$brief_dir/brief-$name.txt"
{ printf '%s' "$header"; cat "$prompt_file"; } > "$brief_path"

# Read-only exploration an agent reaches for without thinking; without these it parks on its first
# `ls`. The PowerShell(...) entries are kept so both twins grant the same profile.
readonly_tools=(
  "Read" "Glob" "Grep"
  "Bash(ls:*)" "Bash(cat:*)" "Bash(head:*)" "Bash(tail:*)" "Bash(grep:*)" "Bash(rg:*)"
  "Bash(find:*)" "Bash(wc:*)" "Bash(sed:-n *)" "Bash(echo:*)" "Bash(which:*)"
  "Bash(git status:*)" "Bash(git diff:*)" "Bash(git log:*)" "Bash(git show:*)"
  "Bash(git branch:*)" "Bash(git rev-parse:*)" "Bash(git ls-files:*)"
  "PowerShell(Get-ChildItem:*)" "PowerShell(Get-Content:*)" "PowerShell(Select-String:*)"
  "PowerShell(Test-Path:*)" "PowerShell(Get-Command:*)" "PowerShell(git status:*)"
  "PowerShell(git diff:*)" "PowerShell(git log:*)"
)
# `git push` is deliberately absent and must stay absent.
fix_tools=(
  "${readonly_tools[@]}"
  "Write" "Edit"
  "Bash(git add:*)" "Bash(git commit:*)"
  "Bash(dotnet build:*)" "Bash(dotnet test:*)" "Bash(pnpm run:*)"
  "PowerShell(git add:*)" "PowerShell(git commit:*)"
  "PowerShell(dotnet build:*)" "PowerShell(dotnet test:*)"
)
profile=()
case "$toolset" in
  readonly) profile=("${readonly_tools[@]}") ;;
  fix)      profile=("${fix_tools[@]}") ;;
esac

# Profile + extras, first occurrence wins (Select-Object -Unique).
tools=()
for t in ${profile[@]+"${profile[@]}"} ${allowed_tools[@]+"${allowed_tools[@]}"}; do
  dup=0
  for u in ${tools[@]+"${tools[@]}"}; do
    if [ "$u" = "$t" ]; then dup=1; break; fi
  done
  if [ "$dup" -eq 0 ]; then tools+=("$t"); fi
done

# Both --allowedTools and --add-dir are variadic. They are safe ONLY because each is followed by
# another flag: --add-dir closes --allowedTools, and --permission-mode closes --add-dir. The prompt
# comes last, after a flag that takes exactly one value. Reorder this and the prompt disappears.
argv=(--bg --name "$name")
[ ${#tools[@]} -eq 0 ]   || argv+=(--allowedTools "${tools[@]}")
[ ${#add_dir[@]} -eq 0 ] || argv+=(--add-dir "${add_dir[@]}")
argv+=(--permission-mode "$permission_mode")
argv+=("Read the file $brief_path and follow it exactly. It is your brief. Start now.")

# The shell line a watch window runs. Same text as Get-WatchLauncher in the .ps1.
watch_line() {
  local id="$1" q
  q="'$(printf '%s' "$repo_root" | sed "s/'/'\\\\''/g")'"
  printf '%s' "cd $q && printf '\n  Attached to $name ($id). Closing this window DETACHES - the agent keeps running.\n\n' && claude attach $id; printf '\n--- detached. The agent is still running; claude agents --json to check. ---\n'"
}

# Sets watch_cmd to the launcher argv, or leaves it empty when this OS has no launcher we know.
watch_cmd=()
resolve_watch() {
  local line
  line="$(watch_line "$1")"
  watch_cmd=()
  case "$(uname -s)" in
    Darwin)
      local as="${line//\\/\\\\}"; as="${as//\"/\\\"}"
      watch_cmd=(osascript -e "tell application \"Terminal\" to do script \"$as\"") ;;
    Linux)
      if command -v gnome-terminal >/dev/null 2>&1; then
        watch_cmd=(gnome-terminal -- bash -c "$line; exec bash")
      elif command -v x-terminal-emulator >/dev/null 2>&1; then
        watch_cmd=(x-terminal-emulator -e bash -c "$line; exec bash")
      fi ;;
  esac
  return 0
}

if [ "$dry_run" -eq 1 ]; then
  echo "dry-run: claude is NOT started"
  echo "brief: $brief_path"
  echo "cwd:   $repo_root"
  echo "argv:"
  echo "  [0] claude"
  i=1
  for a in "${argv[@]}"; do echo "  [$i] $a"; i=$((i + 1)); done
  if [ "$watch" -eq 1 ]; then
    resolve_watch '<id>'
    if [ ${#watch_cmd[@]} -gt 0 ]; then
      echo "watch: ${watch_cmd[*]}"
    else
      echo "watch: no terminal launcher found - would warn and print: claude attach <id>"
    fi
  fi
  exit 0
fi

out="$(cd "$repo_root" && claude "${argv[@]}" 2>&1)" || true

# "backgrounded · <id> · <name>", with colour codes around the id.
id="$(printf '%s\n' "$out" | sed -E $'s/\x1b\\[[0-9;]*m//g' \
  | sed -nE 's/.*backgrounded[[:space:]]*(·|\|)[[:space:]]*([^[:space:]]+).*/\2/p')"
id="${id%%$'\n'*}"
[ -n "$id" ] || die "could not read the background id from claude's output:
$out"

# The agent is running from here on. Nothing below may fail the script: a lost Id is a lost agent.
if [ "$watch" -eq 1 ]; then
  resolve_watch "$id"
  if [ ${#watch_cmd[@]} -eq 0 ]; then
    echo "start-nested-agent: warning: could not open a watch window (no terminal launcher found: tried osascript / gnome-terminal / x-terminal-emulator). $name is running; attach yourself with: claude attach $id" >&2
  elif [ "${watch_cmd[0]}" = osascript ]; then
    "${watch_cmd[@]}" >/dev/null 2>&1 \
      || echo "start-nested-agent: warning: could not open a watch window (osascript failed). $name is running; attach yourself with: claude attach $id" >&2
  else
    ( "${watch_cmd[@]}" >/dev/null 2>&1 & ) \
      || echo "start-nested-agent: warning: could not open a watch window (${watch_cmd[0]} failed). $name is running; attach yourself with: claude attach $id" >&2
  fi
fi

echo "Name=$name"
echo "Id=$id"
echo "Brief=$brief_path"
echo "Started=$(date '+%Y-%m-%dT%H:%M:%S%z')"
