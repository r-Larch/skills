<#
.SYNOPSIS
Stop a nested agent, and optionally delete it.

.DESCRIPTION
Ask the agent to WRAP UP and wait for its DONE before running this. `claude stop` ends the turn it is
in; whatever it had not committed is lost.

Stopping KEEPS the conversation: `claude attach <id>` reopens it and `claude --resume` works once it
is stopped. That is why -Remove is separate and opt-in - a stopped agent is still readable evidence.

.EXAMPLE
.\Stop-NestedAgent.ps1 -Id c88dd0c8 -Name fix-B-07
.\Stop-NestedAgent.ps1 -Id c88dd0c8 -Name fix-B-07 -Remove      # and delete the session
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Id,
    [string] $Name,
    [string] $RepoRoot = (Get-Location).Path,

    # Delete the session after stopping it. Its transcript on disk survives either way.
    [switch] $Remove,

    # Leave the brief in place. Worth keeping when the agent FAILED: it is the record of what the
    # agent was actually told, which is what you read to work out why.
    [switch] $KeepBrief
)

$ErrorActionPreference = "Continue"

$live = (& claude agents --json 2>&1 | Out-String)
if ($live -match [regex]::Escape($Id)) {
    if ($PSCmdlet.ShouldProcess("agent $Id ($Name)", "claude stop")) {
        & claude stop $Id 2>&1 | Out-String | Write-Verbose
        Write-Host "stopped $Name ($Id)" -ForegroundColor DarkGray
    }
} else {
    Write-Host "agent $Id is not listed; already stopped or removed" -ForegroundColor DarkGray
}

if ($Remove -and $PSCmdlet.ShouldProcess("agent $Id", "claude rm")) {
    & claude rm $Id 2>&1 | Out-String | Write-Verbose
    Write-Host "removed $Id" -ForegroundColor DarkGray
}

if ($Name -and -not $KeepBrief) {
    Remove-Item -ErrorAction SilentlyContinue (Join-Path $RepoRoot ".claude\briefs\brief-$Name.txt")
    Remove-Item -ErrorAction SilentlyContinue (Join-Path ([System.IO.Path]::GetTempPath()) "nested-watch-$Name.ps1")
}

# A watch window is only a view. It does not need closing for the agent to stop, and closing it never
# stopped the agent in the first place.
