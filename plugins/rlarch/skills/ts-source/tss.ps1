#!/usr/bin/env pwsh
# tss.ps1 — launcher (Windows / pwsh). The unix twin is tss.sh.
#
# Deliberately trivial: unlike dotnet-source there is NO build step and NO package install. The tool
# is plain ES modules, and the TypeScript compiler it analyses with is the one the target project
# already resolves. Nothing to cache, nothing to invalidate, ~150 ms cold start.

$ErrorActionPreference = 'Stop'

$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    [Console]::Error.WriteLine('ts-source: node is not on PATH (Node 18+ required).')
    exit 2
}

& $node.Source (Join-Path $PSScriptRoot 'tool/cli.mjs') @args
exit $LASTEXITCODE
