#!/usr/bin/env bash
# stop-nested-agent.sh — stop a nested agent, and optionally delete it (macOS / Linux).
# The Windows twin is Stop-NestedAgent.ps1; see it for the rationale.
#
# Ask the agent to WRAP UP and wait for its DONE first: `claude stop` ends the turn it is in, and
# whatever it had not committed is lost. Stopping KEEPS the conversation; --remove deletes it.
#
#   stop-nested-agent.sh --id c88dd0c8 --name fix-B-07
#   stop-nested-agent.sh --id c88dd0c8 --name fix-B-07 --remove     # and delete the session
set -uo pipefail     # no -e: like the .ps1 (ErrorActionPreference=Continue), one failed step must not skip the rest

usage() {
  echo "usage: stop-nested-agent.sh --id <id> [--name <name>] [--repo-root <dir>] [--remove] [--keep-brief] [--dry-run]" >&2
  exit 2
}

id="" name=""
repo_root="$(pwd)"
remove=0
keep_brief=0
dry_run=0

while [ $# -gt 0 ]; do
  case "$1" in
    --id)          [ $# -ge 2 ] || usage; id="$2"; shift 2 ;;
    --name)        [ $# -ge 2 ] || usage; name="$2"; shift 2 ;;
    --repo-root)   [ $# -ge 2 ] || usage; repo_root="$2"; shift 2 ;;
    --remove)      remove=1; shift ;;
    # Leave the brief in place. Worth keeping when the agent FAILED: it is the record of what the
    # agent was actually told.
    --keep-brief)  keep_brief=1; shift ;;
    --dry-run)     dry_run=1; shift ;;
    -h|--help)     usage ;;
    *)             echo "stop-nested-agent: unknown argument: $1" >&2; usage ;;
  esac
done
[ -n "$id" ] || { echo "stop-nested-agent: --id is required" >&2; usage; }

repo_root="${repo_root%/}"
brief_path="$repo_root/.claude/briefs/brief-$name.txt"

if [ "$dry_run" -eq 1 ]; then
  echo "dry-run: no claude command is run, nothing is deleted"
  echo "would run: claude agents --json   (stop only if $id is listed)"
  echo "would run: claude stop $id"
  if [ "$remove" -eq 1 ]; then echo "would run: claude rm $id"; fi
  if [ -n "$name" ] && [ "$keep_brief" -eq 0 ]; then echo "would remove: $brief_path"; fi
  exit 0
fi

live="$(claude agents --json 2>&1)"
if printf '%s' "$live" | grep -qF -- "$id"; then
  claude stop "$id" >/dev/null 2>&1
  echo "stopped $name ($id)"
else
  echo "agent $id is not listed; already stopped or removed"
fi

if [ "$remove" -eq 1 ]; then
  claude rm "$id" >/dev/null 2>&1
  echo "removed $id"
fi

if [ -n "$name" ] && [ "$keep_brief" -eq 0 ]; then
  rm -f "$brief_path"
fi

# A watch window is only a view. It does not need closing for the agent to stop, and closing it never
# stopped the agent in the first place.
