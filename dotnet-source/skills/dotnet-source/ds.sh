#!/usr/bin/env bash
# ds.sh — bootstrap launcher (macOS / Linux). The Windows twin is ds.ps1.
#
# Builds the tool ONCE into a hash-keyed cache and reuses the compiled binary forever after.
# See ds.ps1 for the full rationale; the two must stay behaviourally identical.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tool_dir="$here/tool"
cache_root="${DOTNET_SOURCE_CACHE:-${TMPDIR:-/tmp}/dotnet-source}"

# ---- cache key ---------------------------------------------------------------------------
# tool sources + csproj (pins the Roslyn versions) + the installed runtime band (a
# framework-dependent binary is tied to its runtime major, so an SDK bump must invalidate).
hash_input() {
  find "$tool_dir" -type f \( -name '*.cs' -o -name '*.csproj' \) | LC_ALL=C sort | while read -r f; do
    basename "$f"
    cat "$f"
  done
  dotnet --list-runtimes | grep 'Microsoft.NETCore.App' | tail -n 1
}

if command -v sha256sum >/dev/null 2>&1; then
  hash="$(hash_input | sha256sum | cut -c1-16)"
else
  hash="$(hash_input | shasum -a 256 | cut -c1-16)"   # macOS
fi

bin_dir="$cache_root/tool/$hash"
dll="$bin_dir/DotnetSource.dll"
sentinel="$bin_dir/.ok"

# ---- build once --------------------------------------------------------------------------
# Gate on the sentinel, never the directory: a half-finished publish leaves a dir that LOOKS
# built, and we would exec a broken binary forever.
if [ ! -f "$sentinel" ]; then
  mkdir -p "$cache_root/tool"
  lock="$cache_root/tool/.lock-$hash"

  # Agents fire several commands at once; serialize cold starts so they don't tear each other's
  # output. mkdir is atomic on every POSIX filesystem.
  waited=0
  while ! mkdir "$lock" 2>/dev/null; do
    sleep 0.2
    waited=$((waited + 1))
    if [ -f "$sentinel" ]; then break; fi
    if [ "$waited" -gt 600 ]; then    # 2 min: assume the holder died and take over
      rm -rf "$lock" || true
    fi
  done
  trap 'rm -rf "$lock" 2>/dev/null || true' EXIT

  if [ ! -f "$sentinel" ]; then       # double-check: a peer may have won the race
    echo "dotnet-source: building the tool (first run for this version)…" >&2
    staging="$bin_dir.tmp-$$"
    rm -rf "$staging"
    dotnet publish "$tool_dir/DotnetSource.csproj" -c Release -o "$staging" -v q --nologo
    rm -rf "$bin_dir"
    mv "$staging" "$bin_dir"          # atomic on the same filesystem
    touch "$sentinel"                 # last: marks the build complete
  fi

  rm -rf "$lock"; trap - EXIT

  # Housekeeping: one hash-keyed dir per tool version would accumulate forever.
  find "$cache_root/tool" -maxdepth 1 -type d -mtime +14 ! -path "$bin_dir" ! -path "$cache_root/tool" \
    -exec rm -rf {} + 2>/dev/null || true
fi

exec dotnet "$dll" "$@"
