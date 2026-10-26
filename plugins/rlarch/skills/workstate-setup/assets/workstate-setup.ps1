<#
.SYNOPSIS
  Install, start, register and prove a local WorkState server.
.DESCRIPTION
  Twin of workstate-setup.sh: same steps, same flags, same status lines. Change both together.

  Every line this prints is safe to show a model: tokens and passwords stay in the .env file and in
  the harness's own config, and nothing here echoes them (SKILL.md "What the model never sees").

  -Clone <path>          use a clone of github.com/r-Larch/skills: <path>/mcp/compose.yml (builds the
                         image) and <path>/mcp/.env. Without it: the WorkState home.
  -HomeDir <dir>         the WorkState home (default %USERPROFILE%\.workstate): compose.yml (pulls the
                         published image) and .env
  -Role <role>           orchestrator (default) | controller | worker - whose token to register
  -Name <name>           registration name (default workstate)
  -Agent <session>       register at LOCAL scope (this directory's project only) with the header
                         X-WorkState-Agent: <session>, so sessions here are recorded by that name
  -Port <port>           host port, written only into a NEW .env (default 5290)
  -Project <name>        compose project (default workstate)
  -ImageVersion <tag>    image tag for the pull-only compose (default latest)
  -Update                pull the image again (home) or rebuild it (clone) before starting
  -Force                 replace an existing registration of the same name
  -SessionNames          re-register an existing registration with the session helper (a new one gets
                         it anyway), keeping the role its token has unless -Role is given
  -DryRun               do everything except write the registration; show it with the token masked
  -Backup <file>         pg_dump -Fc the database to <file>, and stop
  -Restore <file>        restore <file> over the database (after a safety backup), and stop
#>
[CmdletBinding()]
param(
    [string]$Clone,
    [string]$HomeDir,
    [ValidateSet('orchestrator', 'controller', 'worker')][string]$Role = 'orchestrator',
    [string]$Name = 'workstate',
    [string]$Agent,
    [ValidateRange(1, 65535)][int]$Port = 5290,
    [string]$Project = 'workstate',
    [string]$ImageVersion,
    [switch]$Update,
    [switch]$Force,
    [switch]$SessionNames,
    [switch]$DryRun,
    [string]$Backup,
    [string]$Restore
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 3

function Die([string]$Line) { [Console]::Error.WriteLine($Line); exit 1 }
function Status([string]$Step, [string]$Text) { Write-Output ('{0,-10}{1}' -f $Step, $Text) }

$scriptDir = $PSScriptRoot
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

if ($Clone -and $HomeDir) { Die '-Clone and -HomeDir are two different installs; pass one' }

function Get-EnvValue([string]$Key) {
    $value = $null
    foreach ($line in [IO.File]::ReadAllLines($script:envFile)) {
        if ($line.StartsWith("$Key=")) { $value = $line.Substring($Key.Length + 1).TrimEnd("`r") }
    }
    $value
}

function Find-Container([string]$Service) {
    $id = & docker ps -q --filter "label=com.docker.compose.project=$Project" --filter "label=com.docker.compose.service=$Service" 2>$null
    if ($id) { @($id)[0] } else { $null }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die 'docker    not found: install Docker Desktop (or Docker Engine) first' }
& docker info *> $null
if ($LASTEXITCODE -ne 0) { Die 'docker    not reachable: start Docker Desktop (Linux: sudo systemctl start docker) and run this again' }

# a. where the install lives
if ($Clone) {
    if (-not (Test-Path -LiteralPath (Join-Path $Clone 'mcp/compose.yml'))) { Die "home      $Clone/mcp/compose.yml not found: -Clone must be the repo root" }
    $Clone = (Resolve-Path -LiteralPath $Clone).Path
    $composeFile = Join-Path $Clone 'mcp/compose.yml'
    $envFile = Join-Path $Clone 'mcp/.env'
    & git -C $Clone check-ignore -q mcp/.env 2>$null
    if ($LASTEXITCODE -ne 0) { Die "home      $Clone/mcp/.env is NOT gitignored: stop and tell the user; a git add would publish the tokens" }
    $mode = 'clone'
}
else {
    if (-not $HomeDir) { $HomeDir = Join-Path $HOME '.workstate' }
    New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null
    $HomeDir = (Resolve-Path -LiteralPath $HomeDir).Path
    $composeFile = Join-Path $HomeDir 'compose.yml'
    $envFile = Join-Path $HomeDir '.env'
    $mode = 'home'
}
$baseDir = Split-Path -Parent $envFile

function Invoke-Compose { & docker compose -p $Project -f $composeFile --env-file $envFile @args }

if ($Backup -or $Restore) {
    if ($Backup -and $Restore) { Die '-Backup and -Restore: pass one' }
    $db = Find-Container 'db'
    if (-not $db) { Die "backup    no running db container in compose project '$Project'" }
    function Save-Dump([string]$File) {
        & docker exec $db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f /tmp/workstate.dump' | Out-Null
        if ($LASTEXITCODE -ne 0) { Die "backup    pg_dump failed in container $db" }
        & docker cp "${db}:/tmp/workstate.dump" $File | Out-Null
        if ($LASTEXITCODE -ne 0) { Die "backup    docker cp to $File failed" }
        & docker exec $db rm -f /tmp/workstate.dump | Out-Null
    }
    if ($Backup) {
        $dir = Split-Path -Parent ([IO.Path]::GetFullPath($Backup))
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $Backup = [IO.Path]::GetFullPath($Backup)
        Save-Dump $Backup
        Status 'backup' "wrote $Backup ($((Get-Item -LiteralPath $Backup).Length) bytes, pg_dump -Fc of project '$Project')"
        exit 0
    }
    if (-not (Test-Path -LiteralPath $Restore)) { Die "restore   $Restore not found" }
    $Restore = (Resolve-Path -LiteralPath $Restore).Path
    $backups = Join-Path $baseDir 'backups'
    New-Item -ItemType Directory -Force -Path $backups | Out-Null
    $safety = Join-Path $backups ("pre-restore-{0}.dump" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Save-Dump $safety
    Status 'backup' "safety copy of the current database: $safety"
    $api = Find-Container 'api'
    if ($api) { & docker stop $api | Out-Null }
    & docker cp $Restore "${db}:/tmp/workstate-restore.dump" | Out-Null
    $log = Join-Path $backups 'restore.log'
    & docker exec $db sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner /tmp/workstate-restore.dump' 2> $log | Out-Null
    $rc = $LASTEXITCODE
    & docker exec $db rm -f /tmp/workstate-restore.dump | Out-Null
    if ($api) { & docker start $api | Out-Null }
    if ($rc -ne 0) { Die "restore   pg_restore exited $rc - see $log; the safety copy is $safety" }
    Status 'restore' "restored $Restore into project '$Project'; api restarted"
    exit 0
}

if ($mode -eq 'home') {
    $shipped = Join-Path $scriptDir 'compose.yml'
    if (-not (Test-Path -LiteralPath $composeFile)) {
        Copy-Item -LiteralPath $shipped -Destination $composeFile
        Status 'home' "$HomeDir (pull-only compose written)"
    }
    elseif ((Get-FileHash -LiteralPath $shipped).Hash -ne (Get-FileHash -LiteralPath $composeFile).Hash) {
        Copy-Item -LiteralPath $shipped -Destination $composeFile -Force
        Status 'home' "$HomeDir (pull-only compose updated to this skill's version)"
    }
    else { Status 'home' "$HomeDir (pull-only compose)" }
}
else { Status 'home' "$Clone (clone: builds mcp/Dockerfile)" }

# b. the .env - written once, never overwritten
function New-RandomHex {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}
if (Test-Path -LiteralPath $envFile) {
    Status 'env' "kept $envFile (exists; never overwritten)"
}
elseif (& docker volume ls -q --filter "name=^$($Project)_workstate-db-data`$") {
    Die "env       $envFile is missing but project '$Project' already has a database volume, created with another .env: pass -Clone <path> for that install, or copy its .env here, and run again"
}
else {
    $content = @(
        '# Written by workstate-setup. Local-only throwaway credentials for a loopback service; never commit.'
        "WORKSTATE_WORKER_TOKEN=local-worker-$(New-RandomHex)"
        "WORKSTATE_ORCHESTRATOR_TOKEN=local-orchestrator-$(New-RandomHex)"
        "WORKSTATE_CONTROLLER_TOKEN=local-controller-$(New-RandomHex)"
        'WORKSTATE_READER_TOKEN='
        ''
        'WORKSTATE_DASHBOARD_USERNAME=operator'
        "WORKSTATE_DASHBOARD_PASSWORD=local-dashboard-$(New-RandomHex)"
        ''
        "WORKSTATE_API_PORT=$Port"
        'WORKSTATE_BIND=127.0.0.1'
        ''
        'POSTGRES_DB=workstate'
        'POSTGRES_USER=workstate'
        "POSTGRES_PASSWORD=local-$(New-RandomHex)"
    ) -join "`n"
    [IO.File]::WriteAllText($envFile, $content + "`n", (New-Object System.Text.UTF8Encoding $false))
    if ($onWindows) {
        & icacls $envFile /inheritance:r /grant:r "$($env:USERNAME):(F)" *> $null
        $modeNote = 'owner-only ACL'
    }
    else {
        & chmod 600 $envFile
        $modeNote = 'mode 600'
    }
    Status 'env' "wrote ${envFile}: 3 tokens, dashboard and Postgres passwords (values not shown, $modeNote)"
}

# the session helper - copied next to the .env, because the plugin cache path changes per version
$helperShipped = Join-Path $scriptDir 'session-header.mjs'
$helperFile = Join-Path $baseDir 'session-header.mjs'
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $helperFile) -or (Get-FileHash -LiteralPath $helperShipped).Hash -ne (Get-FileHash -LiteralPath $helperFile).Hash) {
    Copy-Item -LiteralPath $helperShipped -Destination $helperFile -Force
}
if ($node) { Status 'helper' "$helperFile (names each session; needs node, found)" }
else { Status 'helper' "$helperFile copied, but node is not on PATH: registering without it, so sessions stay unnamed" }

$portValue = Get-EnvValue 'WORKSTATE_API_PORT'
if (-not $portValue) { $portValue = '5290' }
$baseUrl = "http://127.0.0.1:$portValue"

# c. start and wait for /health
if ($ImageVersion) { $env:WORKSTATE_VERSION = $ImageVersion }
$upArgs = @()
if ($Update) {
    if ($mode -eq 'home') {
        $out = Invoke-Compose pull api 2>&1
        if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine(($out | Out-String)); Die 'start     docker compose pull failed (output above)' }
    }
    else { $upArgs = @('--build') }
}
$out = Invoke-Compose up -d @upArgs 2>&1
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine(($out | Out-String)); Die 'start     docker compose up failed (output above)' }
$health = ''
for ($i = 0; $i -lt 90; $i++) {
    try {
        $health = [string](Invoke-WebRequest -Uri "$baseUrl/health" -TimeoutSec 2 -UseBasicParsing).StatusCode
    }
    catch {
        $response = $_.Exception.PSObject.Properties['Response']
        $health = if ($response -and $response.Value) { [string][int]$response.Value.StatusCode } else { '' }
    }
    if ($health -eq '200') { break }
    Start-Sleep -Seconds 2
}
if ($health -ne '200') { Die "start     $baseUrl/health never answered 200 (last: $(if ($health) { $health } else { 'none' })); docker compose -p $Project logs api" }
Status 'start' "project '$Project' up, $baseUrl/health 200"

# d. register - the token goes from the .env straight into the harness's config
$mcpUrl = "$baseUrl/mcp"
$existing = $false
$scope = 'user'
if ($Agent) { $scope = 'local' }
$getOut = ''
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    if (-not $DryRun) { Die 'register  the claude CLI is not on PATH: install Claude Code, or run this where it is' }
}
else {
    # Read here and never printed: SKILL.md forbids the model this command because it shows the header.
    $getOut = (& claude mcp get $Name 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) { $existing = $true }
    elseif ($getOut -notmatch 'No MCP server named') { Die "register  claude mcp get $Name failed; run claude mcp list to see why" }
    if ($existing -and $scope -eq 'local' -and $getOut -notmatch 'Scope: Local') { $existing = $false }
}

# -SessionNames on an existing registration keeps the role its token already has.
if ($existing -and $SessionNames -and -not $PSBoundParameters.ContainsKey('Role')) {
    foreach ($candidate in 'controller', 'orchestrator', 'worker') {
        $candidateToken = Get-EnvValue "WORKSTATE_$($candidate.ToUpperInvariant())_TOKEN"
        if ($candidateToken -and $getOut.Contains("Bearer $candidateToken")) { $Role = $candidate; break }
    }
}
$tokenKey = "WORKSTATE_$($Role.ToUpperInvariant())_TOKEN"
$token = Get-EnvValue $tokenKey
if (-not $token) { Die "env       $envFile has no $tokenKey" }

function Get-Registration([string]$Bearer) {
    $headers = [ordered]@{ Authorization = "Bearer $Bearer" }
    if ($Agent) { $headers['X-WorkState-Agent'] = $Agent }
    $entry = [ordered]@{ type = 'http'; url = $mcpUrl; headers = $headers }
    if ($node) { $entry['headersHelper'] = "node `"$($helperFile.Replace('\', '/'))`"" }
    $entry | ConvertTo-Json -Compress
}
$shown = "claude mcp add-json --scope $scope $Name '$(Get-Registration '***')'"

if ($existing -and -not ($Force -or $SessionNames)) {
    Status 'register' "kept the existing '$Name' registration (pass -SessionNames to add the session helper, -Force to replace it)"
}
elseif ($DryRun) {
    Status 'register' "dry run, would run: $shown"
}
else {
    if ($existing) {
        & claude mcp remove $Name --scope $scope *> $null
        if ($LASTEXITCODE -ne 0) { Die "register  claude mcp remove $Name --scope $scope failed (is it registered in another scope?)" }
    }
    $json = Get-Registration $token
    # Windows PowerShell 5.1 strips the JSON's quotes from a native argument unless they are escaped.
    if ($PSVersionTable.PSVersion.Major -lt 7 -or (Get-Variable PSNativeCommandArgumentPassing -ValueOnly -ErrorAction SilentlyContinue) -eq 'Legacy') {
        $json = $json.Replace('"', '\"')
    }
    & claude mcp add-json --scope $scope $Name $json *> $null
    if ($LASTEXITCODE -ne 0) { Die "register  claude mcp add-json failed: $shown" }
    Status 'register' "'$Name' -> $mcpUrl as $Role$(if ($Agent) { ", agent $Agent" })$(if ($node) { ', session helper' }) ($scope scope; token stored by Claude Code, not shown)"
}

# e. prove - count what the server lists for that token
$body = ''
try {
    $body = (Invoke-WebRequest -Uri $mcpUrl -Method Post -TimeoutSec 10 -UseBasicParsing -ContentType 'application/json' `
            -Headers @{ Accept = 'application/json, text/event-stream'; Authorization = "Bearer $token" } `
            -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}').Content
    if ($body -is [byte[]]) { $body = [Text.Encoding]::UTF8.GetString($body) }
}
catch { $body = '' }
$count = @([regex]::Matches([string]$body, '"name":"work_[a-z_]*"') | ForEach-Object { $_.Value } | Sort-Object -Unique).Count
switch ($count) {
    19 { $implied = 'orchestrator/controller' }
    6 { $implied = 'worker' }
    0 { Die "prove     tools/list returned no work_* tool for the $Role token: the token or the server is wrong; docker compose -p $Project logs api" }
    default { $implied = 'unexpected - an image older or newer than these skills (run with -Update)' }
}
Status 'prove' "$count work_* tools for the $Role token: $implied"

# f. what the human needs next
Status 'dashboard' "$baseUrl - sign in with WORKSTATE_DASHBOARD_USERNAME / _PASSWORD from $envFile"
Status 'next' 'start a new Claude Code session: the work_* tools appear there, not in this one'
