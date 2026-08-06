#!/usr/bin/env pwsh
# ds.ps1 — bootstrap launcher (Windows / pwsh). The unix twin is ds.sh.
#
# Builds the tool ONCE into a hash-keyed cache and reuses the compiled binary forever after.
# This is deliberately NOT `dotnet run tool.cs`: the analysis code references Roslyn, and we want
# one compiled multi-command process that can share a parse pass and hold a Solution in memory.
#
#   hash = tool sources + csproj (which pins the Roslyn versions) + the installed runtime band
#   -> $TEMP/dotnet-source/tool/<hash>/DotnetSource.dll
#
# The hash covers the runtime because a framework-dependent binary is tied to its runtime major:
# an SDK bump must invalidate the cache, not silently reuse a binary built for the old one.

$ErrorActionPreference = 'Stop'

$toolDir = Join-Path $PSScriptRoot 'tool'
$cacheRoot = if ($env:DOTNET_SOURCE_CACHE) { $env:DOTNET_SOURCE_CACHE }
             else { Join-Path ([System.IO.Path]::GetTempPath()) 'dotnet-source' }

# ---- cache key ---------------------------------------------------------------------------
$sb = [System.Text.StringBuilder]::new()
Get-ChildItem $toolDir -File -Recurse |
    Where-Object { $_.Extension -in '.cs', '.csproj' } |
    Sort-Object FullName |
    ForEach-Object {
        [void]$sb.Append($_.Name)
        [void]$sb.Append([System.IO.File]::ReadAllText($_.FullName))
    }
[void]$sb.Append(((& dotnet --list-runtimes) -match 'Microsoft\.NETCore\.App' | Select-Object -Last 1))

$bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
$hash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 16).ToLower()

$binDir = Join-Path $cacheRoot "tool/$hash"
$dll = Join-Path $binDir 'DotnetSource.dll'
$sentinel = Join-Path $binDir '.ok'

# ---- build once --------------------------------------------------------------------------
# Gate on the sentinel, never on the directory: a failed or half-finished publish leaves a
# directory that LOOKS built, and we'd exec a broken binary forever.
if (-not (Test-Path $sentinel)) {
    # Agents fire several commands at once. Without this, concurrent cold starts publish into the
    # same folder and tear each other's output.
    $mutex = [System.Threading.Mutex]::new($false, "Global\dotnet-source-$hash")
    [void]$mutex.WaitOne()
    try {
        if (-not (Test-Path $sentinel)) {                    # double-check: a peer may have won
            # stderr, never stdout: stdout is the greppable result stream.
            [Console]::Error.WriteLine("dotnet-source: building the tool (first run for this version)…")
            $staging = "$binDir.tmp-$PID"
            if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

            & dotnet publish "$toolDir/DotnetSource.csproj" -c Release -o $staging -v q --nologo
            if ($LASTEXITCODE -ne 0) { throw "dotnet-source: failed to build the tool (see the output above)." }

            New-Item -ItemType Directory -Force -Path (Split-Path $binDir) | Out-Null
            if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
            [System.IO.Directory]::Move($staging, $binDir)   # atomic on the same volume
            New-Item -ItemType File -Path $sentinel | Out-Null   # last: marks the build complete
        }
    }
    finally { $mutex.ReleaseMutex(); $mutex.Dispose() }

    # Housekeeping: hash-keyed dirs would otherwise accumulate one per tool version, forever.
    Get-ChildItem (Join-Path $cacheRoot 'tool') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $binDir -and $_.LastWriteTimeUtc -lt (Get-Date).AddDays(-14).ToUniversalTime() } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

& dotnet $dll @args
exit $LASTEXITCODE
