#!/usr/bin/env bash
# tss.sh — launcher (macOS / Linux). The Windows twin is tss.ps1.
#
# Deliberately trivial: unlike dotnet-source there is NO build step and NO package install. The tool
# is plain ES modules, and the TypeScript compiler it analyses with is the one the target project
# already resolves. Nothing to cache, nothing to invalidate, ~150 ms cold start.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v node >/dev/null 2>&1; then
  echo "ts-source: node is not on PATH (Node 18+ required)." >&2
  exit 2
fi

exec node "$here/tool/cli.mjs" "$@"
