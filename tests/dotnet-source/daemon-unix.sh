#!/usr/bin/env bash
# daemon-unix.sh — gate for the dotnet-source `serve` daemon on macOS/Linux.
#
# Builds the tool OUTSIDE the repo (the repo's tool/bin|obj belong to the Windows build), serves a
# throwaway sample project and checks that the detached daemon outlives its caller and recovers
# from a hard kill. One PASS/FAIL line per check; exits non-zero on any FAIL.
#
#   wsl.exe -e bash tests/dotnet-source/daemon-unix.sh          (from Windows, cwd = repo root)
#   DS_TOOL_SRC=/some/dir bash tests/dotnet-source/daemon-unix.sh  (gate another copy of the tool)
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="${DS_TOOL_SRC:-$here/../../plugins/rlarch/skills/dotnet-source/tool}"
[ -d "$HOME/.dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

work="$(mktemp -d "${TMPDIR:-/tmp}/ds-gate.XXXXXX")"
export DOTNET_SOURCE_CACHE="$work/cache"   # markers, logs and index stay inside the gate
out="$work/out"
dll="$out/DotnetSource.dll"
proj="$work/sample/Sample.csproj"
fails=0

# The bracket keeps the pattern from matching this shell's own command line.
daemons() { pgrep -f "$out/DotnetSource.dll serve --foregroun[d]" | tr '\n' ' ' | sed 's/ $//'; }
cleanup() {
  for p in $(daemons); do kill -9 "$p" 2>/dev/null; done
  [ -n "${pipe:-}" ] && rm -f "${TMPDIR:-/tmp}/CoreFxPipe_$pipe"
  rm -rf "$work"
}
trap cleanup EXIT
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; fails=$((fails + 1)); }
alive() { [ -n "$1" ] && kill -0 "$1" 2>/dev/null; }
ds() { timeout 150 dotnet "$dll" "$@"; }
# Between checks: no daemon and no marker, so each check fails or passes on its own.
stop_all() { ds serve --stop --proj "$proj" >/dev/null 2>&1; sleep 1; for p in $(daemons); do kill -9 "$p" 2>/dev/null; done; sleep 0.5; rm -f "$DOTNET_SOURCE_CACHE/daemons/"*.pid; }

# A caller in its own session, like a harness running one command: serve, report its exit, linger.
start_caller() {   # $1 = output file; echoes the caller's pid (= its pgid = its sid)
  setsid bash -c "dotnet '$dll' serve --proj '$proj'; echo serve-exit=\$?; sleep 300" >"$1" 2>&1 &
  local leader=$!
  for _ in $(seq 1 150); do grep -q "serve-exit" "$1" && break; sleep 1; done
  echo "$leader"
}

# ---- build + sample ----------------------------------------------------------------------
mkdir -p "$work/src" "$work/sample"
(cd "$src" && cp -- *.cs *.csproj "$work/src/")
if ! dotnet publish "$work/src/DotnetSource.csproj" -c Release -o "$out" -v q --nologo >"$work/build.log" 2>&1; then
  cat "$work/build.log"; echo "FAIL build"; exit 1
fi
cat >"$proj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
EOF
cat >"$work/sample/Widget.cs" <<'EOF'
namespace Sample;
public class Widget { public int Size() => 1; }
public class User { public int Use(Widget w) => w.Size(); }
EOF
pipe="$(ds status --proj "$proj" | awk '$1 == "pipe" { print $2 }')"
socket="${TMPDIR:-/tmp}/CoreFxPipe_$pipe"
markers() { ls "$DOTNET_SOURCE_CACHE/daemons/"*.pid 2>/dev/null | wc -l | tr -d ' '; }

# ---- (a) own session --------------------------------------------------------------------
caller="$(start_caller "$work/a.out")"
d="$(daemons)"
if grep -q "serve-exit=0" "$work/a.out" && alive "$d"; then
  read -r dpgid dsid < <(ps -o pgid=,sid= -p "$d")
  cpgid="$(ps -o pgid= -p "$caller" | tr -d ' ')"
  if [ "$dsid" = "$d" ] && [ "$dpgid" != "$cpgid" ]; then
    pass "(a) daemon $d runs in its own session (sid $dsid, pgid $dpgid; caller pgid $cpgid)"
  else
    fail "(a) daemon $d shares the caller's session/group (sid $dsid, pgid $dpgid; caller pid/pgid $caller/$cpgid)"
  fi
else
  fail "(a) serve did not start a daemon: $(tr '\n' ' ' <"$work/a.out")"
fi

# ---- (b) kill the caller's process group -------------------------------------------------
kill -- -"$caller" 2>/dev/null; sleep 2
if alive "$d" && ds status --proj "$proj" | grep -q "projects,"; then
  pass "(b) daemon $d survives a kill of the caller's process group and still answers"
else
  fail "(b) daemon died with the caller's process group (daemons now: [$(daemons)])"
fi

# ---- (c) SIGHUP to the caller's session -------------------------------------------------
stop_all
caller="$(start_caller "$work/c.out")"
d="$(daemons)"
kill -HUP -- -"$caller" 2>/dev/null; sleep 1
kill -HUP "$d" 2>/dev/null; sleep 2
if alive "$d" && ds status --proj "$proj" | grep -q "projects,"; then
  pass "(c) daemon $d survives SIGHUP to the caller's session and to itself"
else
  fail "(c) daemon [$d] did not survive SIGHUP (daemons now: [$(daemons)])"
fi
kill -9 -- -"$caller" 2>/dev/null

# ---- (d) a hard-killed daemon does not block the next serve ------------------------------
stop_all
ds serve --proj "$proj" >/dev/null 2>&1
old="$(daemons)"
kill -9 "$old" 2>/dev/null; sleep 1
left="$(markers)"
# The killed daemon also leaves its unix socket; the new one must still bind the same pipe name.
sock="$([ -S "$socket" ] && echo left || echo gone)"
ds serve --proj "$proj" >"$work/d.out" 2>&1; rc=$?
new="$(daemons)"
if [ "$rc" = 0 ] && alive "$new" && [ "$new" != "$old" ] && ds status --proj "$proj" | grep -q "projects,"; then
  pass "(d) after kill -9 of $old (markers left: $left, socket $sock) the next serve started $new and it answers"
else
  fail "(d) after kill -9 of $old the next serve exited $rc, daemons [$new]: $(head -3 "$work/d.out" | tr '\n' ' ')"
fi

# ---- (e) serves at the same instant -> one daemon ----------------------------------------
# Twice: from a clean slate, and from the stale marker a kill -9 leaves, where "delete the stale
# marker, then create" would let two contenders both win.
for start in clean stale; do
  if [ "$start" = clean ]; then stop_all; else for p in $(daemons); do kill -9 "$p" 2>/dev/null; done; sleep 1; fi
  pids=()
  for i in 1 2 3; do ds serve --proj "$proj" >"$work/e-$start-$i.out" 2>&1 & pids+=($!); done
  rcs=""; for p in "${pids[@]}"; do wait "$p"; rcs="$rcs$?"; done
  sleep 1
  n="$(daemons | wc -w | tr -d ' ')"
  if [ "$rcs" = 000 ] && [ "$n" = 1 ]; then
    pass "(e) three concurrent serves ($start start) all exited 0 and left exactly one daemon ($(daemons))"
  else
    fail "(e) three concurrent serves ($start start) exited $rcs and left $n daemon(s) [$(daemons)]"
  fi
done

# ---- (f) --stop leaves nothing behind ----------------------------------------------------
ds serve --stop --proj "$proj" >/dev/null 2>&1; sleep 2
n="$(daemons | wc -w | tr -d ' ')"
m="$(markers)"
if [ "$n" = 0 ] && [ "$m" = 0 ] && [ ! -e "$socket" ]; then
  pass "(f) serve --stop left no daemon, no marker and no socket"
else
  fail "(f) after serve --stop: $n daemon(s), $m marker(s), socket $( [ -e "$socket" ] && echo present || echo absent)"
fi

echo "$([ "$fails" = 0 ] && echo GREEN || echo "RED ($fails failed)")"
exit "$fails"
