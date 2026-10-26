<#
.SYNOPSIS
Start a nested Claude Code agent as a background session, optionally with a window attached to watch.

.DESCRIPTION
`claude --bg` returns a short id immediately and registers the session under -Name, so the controller
can address it by name from the first second and drive it with claude's own lifecycle commands
(agents / logs / attach / stop / rm / respawn). The agent outlives the controller.

-Watch opens a terminal window running `claude attach <id>`. That window is a VIEW: closing it
detaches and leaves the agent running. If no window can be opened the agent still runs: you get a
warning with the attach command, and the object is returned as usual.

-DryRun does everything except run claude: it validates, writes the brief, and prints the exact argv.
The bash twin is start-nested-agent.sh; for the same inputs the two print the same argv.

.EXAMPLE
$a = .\Start-NestedAgent.ps1 -Name fix-B-07 -PromptFile .\brief.txt -Controller 'my-session [9825b9]' -Watch
$a.Id      # short id, for claude stop/logs/attach
$a.Name    # the SendMessage address
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Name,
    [Parameter(Mandatory)][string] $PromptFile,

    # The controller's address exactly as ListAgents prints it, ref included. A bare name is not an
    # address: Remote Control mirrors sessions under the same name, and SendMessage stops to ask
    # which one rather than delivering.
    [Parameter(Mandatory)][string] $Controller,

    [string]   $RepoRoot       = (Get-Location).Path,
    [string]   $PermissionMode = "auto",

    # A named tool profile, merged with -AllowedTools. A --bg agent has no human at its keyboard, so
    # the FIRST shell call it has not been granted parks it - see the -Toolset table in SKILL.md.
    # "none" is only right for an agent that never shells out.
    [ValidateSet("none","readonly","fix")]
    [string]   $Toolset        = "fix",

    [string[]] $AllowedTools   = @(),

    # Extra directories the agent may read. An allow-list grants TOOLS, not PATHS: `Bash(ls:*)` still
    # prompts the moment the target is outside -RepoRoot. Package caches are the usual case.
    [string[]] $AddDir         = @(),

    [switch]   $Watch,

    [switch]   $DryRun
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw "claude is not on PATH." }
if (-not (Test-Path $PromptFile)) { throw "Prompt file not found: $PromptFile" }

# -RepoRoot must already be a working tree. Without this check a failed `git worktree add` upstream
# still yields a launch: the brief directory is created with -Force, the agent starts in a folder
# holding nothing but its own brief, and reports READY. A setup failure must not produce a confident
# agent - it must fail here, loudly, before anything is spawned.
if (-not (Test-Path $RepoRoot)) { throw "-RepoRoot does not exist: $RepoRoot. Did `git worktree add` fail?" }
if (-not (Test-Path (Join-Path $RepoRoot ".git"))) {
    throw "-RepoRoot is not a git working tree: $RepoRoot. A --bg agent must be launched inside one - see SKILL.md section 7."
}
if ($Controller -notmatch '^\S.*\s\[[0-9a-f]{6}\]$') {
    throw "-Controller must include the ref, e.g. 'my-session [9825b9]'. Run ListAgents and copy the first line."
}

$header = @"
Your controller is the Claude Code session '$Controller'. It started you, it is watching you, and it
is who you report to and ask. If a human attaches to this session they are observing, not directing:
questions go to the controller. SendMessage with to="$Controller", verbatim, brackets included.

Send exactly these, and no progress chatter - the controller reads your transcript for that:

  READY <task>   once, immediately, before any other work.
  BLOCKED <what> the moment you need a decision that is not yours - the task looks wrong, it needs a
                 schema change or a production write, the scope is ambiguous. Then STOP and wait for
                 the reply. Do not guess and carry on.
  DONE <report>  once, at the end: what you changed with file:line, the test that covers it, the
                 exact build and test result, and anything you could not do.

Open every message with your name '$Name'.

If the controller sends you WRAP UP: stop at the next safe point, commit what is finished, and send
DONE saying exactly where you stopped. Do not start anything new.

You have no human at a keyboard. If you hit a permission prompt you cannot clear, do not work around
it and do not ask a peer to act for you - send BLOCKED naming the exact operation, and wait.

"@
# LF, so the brief is byte-identical to the one start-nested-agent.sh writes; this file is CRLF on disk.
$header = $header -replace "`r`n", "`n"

# Inside the repo on purpose. A brief under $env:TEMP makes the agent's first Read raise a permission
# prompt, and --add-dir cannot buy its way out: it takes <directories...>, so it swallows the prompt
# that follows as another directory and the agent launches with no instructions at all.
$briefDir  = Join-Path (Join-Path $RepoRoot '.claude') 'briefs'
New-Item -ItemType Directory -Force -Path $briefDir | Out-Null
$briefPath = Join-Path $briefDir "brief-$Name.txt"
[System.IO.File]::WriteAllText($briefPath, $header + (Get-Content -Raw -Encoding UTF8 $PromptFile),
    [System.Text.UTF8Encoding]::new($false))

# One quote-free line naming the brief. A multi-line prompt passed positionally is truncated at its
# first quote, and this brief's own header contains quotes.
# Read-only exploration an agent reaches for without thinking. Without these it parks on its first
# `ls`, and claude agents --json shows status=waiting, waitingFor="permission prompt".
$readonly = @(
    "Read", "Glob", "Grep",
    "Bash(ls:*)", "Bash(cat:*)", "Bash(head:*)", "Bash(tail:*)", "Bash(grep:*)", "Bash(rg:*)",
    "Bash(find:*)", "Bash(wc:*)", "Bash(sed:-n *)", "Bash(echo:*)", "Bash(which:*)",
    "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)",
    "Bash(git branch:*)", "Bash(git rev-parse:*)", "Bash(git ls-files:*)",
    # On Windows the agent reaches for the PowerShell tool as readily as Bash, and a Bash(...)
    # pattern does not cover it. Note this only widens the allow-list: some prompts come from tool
    # guards rather than the allow-list ("script block that may execute arbitrary code") and no
    # profile clears those - the brief has to stop the agent wanting them. See SKILL.md §3.
    "PowerShell(Get-ChildItem:*)", "PowerShell(Get-Content:*)", "PowerShell(Select-String:*)",
    "PowerShell(Test-Path:*)", "PowerShell(Get-Command:*)", "PowerShell(git status:*)",
    "PowerShell(git diff:*)", "PowerShell(git log:*)"
)
# Writing and verifying. `git push` is deliberately absent and must stay absent: an allow-list is the
# only thing between a nested agent and a remote its controller was told never to touch.
$fix = $readonly + @(
    "Write", "Edit",
    "Bash(git add:*)", "Bash(git commit:*)",
    "Bash(dotnet build:*)", "Bash(dotnet test:*)", "Bash(pnpm run:*)",
    "PowerShell(git add:*)", "PowerShell(git commit:*)",
    "PowerShell(dotnet build:*)", "PowerShell(dotnet test:*)"
)
$tools = switch ($Toolset) {
    "readonly" { $readonly }
    "fix"      { $fix }
    default    { @() }
}
$tools = @($tools + $AllowedTools | Select-Object -Unique)

# --allowedTools is variadic too, so a flag has to follow it or it eats the prompt exactly the way
# --add-dir does. --permission-mode is that flag; do not move it back above.
# Both --allowedTools and --add-dir are variadic. They are safe ONLY because each is followed by
# another flag: --add-dir closes --allowedTools, and --permission-mode closes --add-dir. The prompt
# comes last, after a flag that takes exactly one value. Reorder this and the prompt disappears.
$claudeArgs = @("--bg", "--name", $Name)
if ($tools.Count)   { $claudeArgs += @("--allowedTools") + $tools }
if ($AddDir.Count)  { $claudeArgs += @("--add-dir") + $AddDir }
$claudeArgs += @("--permission-mode", $PermissionMode)
$claudeArgs += "Read the file $briefPath and follow it exactly. It is your brief. Start now."

$onWindows = $IsWindows -or $PSVersionTable.PSEdition -eq 'Desktop'
$onMac     = [bool]$IsMacOS

# The window that runs `claude attach <id>`, or $null when this OS has no launcher we know.
function Get-WatchLauncher([string] $id) {
    if ($onWindows) {
        $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell.exe' }
        $watchPath = Join-Path ([System.IO.Path]::GetTempPath()) "nested-watch-$Name.ps1"
        return @{ Exe = $shell; Args = @("-NoExit","-ExecutionPolicy","Bypass","-File",$watchPath); Script = $watchPath }
    }
    # The same shell line start-nested-agent.sh uses, so a watch window looks alike from either twin.
    $q = "'" + ($RepoRoot -replace "'", "'\''") + "'"
    $line = "cd $q && printf '\n  Attached to $Name ($id). Closing this window DETACHES - the agent keeps running.\n\n' && claude attach $id; printf '\n--- detached. The agent is still running; claude agents --json to check. ---\n'"
    if ($onMac) {
        $as = $line -replace '\\', '\\' -replace '"', '\"'
        return @{ Exe = 'osascript'; Args = @("-e", "tell application `"Terminal`" to do script `"$as`"") }
    }
    if (Get-Command gnome-terminal -ErrorAction SilentlyContinue) {
        return @{ Exe = 'gnome-terminal'; Args = @("--", "bash", "-c", "$line; exec bash") }
    }
    if (Get-Command x-terminal-emulator -ErrorAction SilentlyContinue) {
        return @{ Exe = 'x-terminal-emulator'; Args = @("-e", "bash", "-c", "$line; exec bash") }
    }
    return $null
}

if ($DryRun) {
    "dry-run: claude is NOT started"
    "brief: $briefPath"
    "cwd:   $RepoRoot"
    "argv:"
    "  [0] claude"
    for ($i = 0; $i -lt $claudeArgs.Count; $i++) { "  [$($i + 1)] $($claudeArgs[$i])" }
    if ($Watch) {
        $w = Get-WatchLauncher '<id>'
        "watch: " + $(if ($w) { "$($w.Exe) $($w.Args -join ' ')" } else { "no terminal launcher found - would warn and print: claude attach <id>" })
    }
    return
}

Push-Location $RepoRoot
try   { $out = & claude @claudeArgs 2>&1 | Out-String }
finally { Pop-Location }

# "backgrounded · <id> · <name>", with colour codes around the id.
$id = ([regex]::Match(($out -replace '\x1b\[[0-9;]*m',''), 'backgrounded\s*[·|]\s*(\S+)')).Groups[1].Value
if (-not $id) { throw "could not read the background id from claude's output:`n$out" }

# The agent is running from here on. Nothing below may throw: a lost return value is a lost Id.
if ($Watch) {
    try {
        $w = Get-WatchLauncher $id
        if (-not $w) { throw "no terminal launcher found (tried osascript / gnome-terminal / x-terminal-emulator)" }
        if ($w.Script) {
            $inner = @"
`$Host.UI.RawUI.WindowTitle = 'watch $Name'
Set-Location '$RepoRoot'
Write-Host ''
Write-Host '  Attached to $Name ($id). Closing this window DETACHES - the agent keeps running.' -ForegroundColor Cyan
Write-Host '  Answer a permission prompt here if the agent reports it is blocked on one.' -ForegroundColor DarkGray
Write-Host ''
& claude attach '$id'
Write-Host ''
Write-Host '--- detached. The agent is still running; claude agents --json to check. ---' -ForegroundColor DarkGray
"@
            Set-Content -Path $w.Script -Value $inner -Encoding UTF8
        }
        if ($w.Exe -eq 'osascript') {
            & osascript @($w.Args) | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "osascript exited $LASTEXITCODE" }
        } elseif ($onWindows) {
            # A plain console process, NOT wt.exe: Windows Terminal hosts it anyway when it is the default
            # terminal, and `wt.exe new-tab` collapses everything after the tab options into one path.
            # Start-Process joins -ArgumentList with spaces and quotes nothing, hence the quoted -File.
            $winArgs = @($w.Args[0..3]) + "`"$($w.Args[4])`""
            Start-Process $w.Exe -ArgumentList $winArgs | Out-Null
        } else {
            # Not Start-Process: it would split the `bash -c` line at its spaces.
            $psi = [System.Diagnostics.ProcessStartInfo]::new($w.Exe)
            foreach ($a in $w.Args) { $psi.ArgumentList.Add($a) }
            [void][System.Diagnostics.Process]::Start($psi)
        }
    } catch {
        Write-Warning "could not open a watch window ($($_.Exception.Message)). $Name is running; attach yourself with: claude attach $id"
    }
}

[pscustomobject]@{
    Name    = $Name          # the SendMessage address
    Id      = $id            # for claude logs/attach/stop/rm/respawn
    Brief   = $briefPath
    Started = Get-Date
}
