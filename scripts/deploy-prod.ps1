#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Publish Mezube for production on Windows or Linux.

.DESCRIPTION
  Cross-platform production deploy:
  - Detects RID (win-x64 / linux-x64 / linux-arm64 / osx-*)
  - Publishes Release build
  - Preserves existing data/ and temp/ under the output dir
  - Writes run helpers and optional systemd unit (Linux)
  - Optionally installs/starts the service or runs the bot

.EXAMPLE
  ./scripts/deploy-prod.ps1
  ./scripts/deploy-prod.ps1 -Runtime linux-x64 -SelfContained
  ./scripts/deploy-prod.ps1 -OutputDir /opt/mezube -InstallService -Start
  ./scripts/deploy-prod.ps1 -Run
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "",
    [ValidateSet("auto", "win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Runtime = "auto",
    [switch]$SelfContained,
    [switch]$SkipChecks,
    [switch]$SkipPublish,
    [switch]$Run,
    [switch]$InstallService,
    [switch]$Start,
    [switch]$Stop,
    [string]$ServiceName = "mezube",
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-IsWindowsOS {
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        return [bool]$IsWindows
    }
    return $env:OS -eq "Windows_NT"
}

function Test-IsLinuxOS {
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        return [bool]$IsLinux
    }
    return $false
}

function Test-IsMacOS {
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        return [bool]$IsMacOS
    }
    return $false
}

function Write-Utf8File([string]$Path, [string]$Content, [switch]$NoBom) {
    $encoding = if ($NoBom) {
        if ($PSVersionTable.PSVersion.Major -ge 6) {
            "utf8NoBOM"
        }
        else {
            New-Object System.Text.UTF8Encoding $false
        }
    }
    else {
        "utf8"
    }
    if ($encoding -is [System.Text.Encoding]) {
        [System.IO.File]::WriteAllText($Path, $Content, $encoding)
    }
    else {
        Set-Content -Path $Path -Value $Content -Encoding $encoding
    }
}

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok([string]$Message) {
    Write-Host "OK  $Message" -ForegroundColor Green
}

function Write-WarnMsg([string]$Message) {
    Write-Host "WARN $Message" -ForegroundColor Yellow
}

function Get-RepoRoot {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Get-DefaultRuntime {
    if (Test-IsWindowsOS) {
        return "win-x64"
    }
    if (Test-IsMacOS) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        if ("$arch" -eq "Arm64") {
            return "osx-arm64"
        }
        return "osx-x64"
    }
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ("$arch" -eq "Arm64") {
        return "linux-arm64"
    }
    return "linux-x64"
}

function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Assert-Command([string]$Name, [string]$Hint) {
    if (-not (Test-Command $Name)) {
        throw "Required command '$Name' not found. $Hint"
    }
}

function Stop-MezubeProcess([string]$PublishDir, [string]$Name) {
    $pidFile = Join-Path $PublishDir "$Name.pid"
    if (Test-Path $pidFile) {
        $raw = (Get-Content $pidFile -Raw).Trim()
        if ($raw -match '^\d+$') {
            $procId = [int]$raw
            try {
                $proc = Get-Process -Id $procId -ErrorAction Stop
                Write-Step "Stopping existing process pid=$procId ($($proc.ProcessName))"
                Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 1
            }
            catch {
                Write-WarnMsg "PID $procId from $pidFile is not running"
            }
        }
        Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    }

    if ((Test-IsLinuxOS) -and (Test-Command "systemctl")) {
        $unit = "$Name.service"
        $state = & systemctl is-active $unit 2>$null
        if ($LASTEXITCODE -eq 0 -and "$state" -eq "active") {
            Write-Step "Stopping systemd unit $unit"
            & sudo systemctl stop $unit
        }
    }
}

function New-RunScripts([string]$PublishDir, [string]$Rid, [bool]$IsSelfContainedBuild) {
    $exeName = if ($Rid.StartsWith("win-")) { "Mezube.exe" } else { "Mezube" }
    $dllEntry = "Mezube.dll"
    $useAppHost = $IsSelfContainedBuild -or (Test-Path (Join-Path $PublishDir $exeName))

    $runPs1 = @(
        '$ErrorActionPreference = "Stop"'
        'Set-Location $PSScriptRoot'
        '$env:DOTNET_ENVIRONMENT = "prod"'
        'New-Item -ItemType Directory -Force -Path "data","temp" | Out-Null'
    )
    if ($useAppHost) {
        $runPs1 += "& .\$exeName @args"
    }
    else {
        $runPs1 += "dotnet .\$dllEntry @args"
    }
    Write-Utf8File -Path (Join-Path $PublishDir "run.ps1") -Content ($runPs1 -join [Environment]::NewLine)

    $runShLines = @(
        '#!/usr/bin/env bash'
        'set -euo pipefail'
        'cd "$(dirname "$0")"'
        'export DOTNET_ENVIRONMENT=prod'
        'mkdir -p data temp'
    )
    if ($useAppHost) {
        $runShLines += "chmod +x ./$exeName 2>/dev/null || true"
        $runShLines += "exec ./$exeName `"$@`""
    }
    else {
        $runShLines += "exec dotnet ./$dllEntry `"$@`""
    }
    $runShPath = Join-Path $PublishDir "run.sh"
    Write-Utf8File -Path $runShPath -Content ($runShLines -join "`n") -NoBom
    if (-not (Test-IsWindowsOS)) {
        & chmod +x $runShPath
        if ($useAppHost) {
            $exePath = Join-Path $PublishDir $exeName
            if (Test-Path $exePath) { & chmod +x $exePath }
        }
    }
}

function New-SystemdUnit([string]$PublishDir, [string]$Name, [string]$Rid, [bool]$IsSelfContainedBuild) {
    $exeName = if ($Rid.StartsWith("win-")) { "Mezube.exe" } else { "Mezube" }
    $useAppHost = $IsSelfContainedBuild -or (Test-Path (Join-Path $PublishDir $exeName))
    $unixDir = $PublishDir -replace '\\', '/'
    $execStart = if ($useAppHost) {
        "$unixDir/$exeName"
    }
    else {
        "/usr/bin/dotnet $unixDir/Mezube.dll"
    }

    $unit = @"
[Unit]
Description=Mezube Mezon music bot
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$unixDir
Environment=DOTNET_ENVIRONMENT=prod
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$execStart
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=30
# Uncomment and set a dedicated user in production:
# User=mezube
# Group=mezube

[Install]
WantedBy=multi-user.target
"@
    $unitPath = Join-Path $PublishDir "$Name.service"
    Write-Utf8File -Path $unitPath -Content $unit -NoBom
    return $unitPath
}

function Install-SystemdService([string]$UnitPath, [string]$Name) {
    if (-not (Test-IsLinuxOS)) {
        throw "-InstallService is only supported on Linux (systemd)."
    }
    Assert-Command "systemctl" "Install systemd or omit -InstallService."
    Assert-Command "sudo" "sudo is required to install the service."

    $dest = "/etc/systemd/system/$Name.service"
    Write-Step "Installing systemd unit -> $dest"
    & sudo cp $UnitPath $dest
    & sudo systemctl daemon-reload
    & sudo systemctl enable $Name.service
    Write-Ok "Service $Name enabled"
}

function Start-Mezube([string]$PublishDir, [string]$Name, [bool]$AsService) {
    if ($AsService) {
        Write-Step "Starting systemd service $Name"
        & sudo systemctl restart $Name
        & sudo systemctl --no-pager --full status $Name
        return
    }

    Write-Step "Starting Mezube in background (DOTNET_ENVIRONMENT=prod)"
    $pidFile = Join-Path $PublishDir "$Name.pid"
    $logFile = Join-Path $PublishDir "mezube.out.log"
    $runSh = Join-Path $PublishDir "run.sh"
    $runPs1 = Join-Path $PublishDir "run.ps1"

    if (Test-IsWindowsOS) {
        $shell = if (Test-Command "pwsh") { "pwsh" } else { "powershell" }
        $proc = Start-Process -FilePath $shell -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $runPs1
        ) -WorkingDirectory $PublishDir -RedirectStandardOutput $logFile -RedirectStandardError "$logFile.err" -PassThru -WindowStyle Hidden
        Set-Content -Path $pidFile -Value $proc.Id -Encoding ascii
        Write-Ok "Started pid=$($proc.Id), log=$logFile"
        return
    }

    Assert-Command "bash" "bash is required to background-run on Linux/macOS."
    $bashCmd = "nohup bash `"$runSh`" > `"$logFile`" 2>&1 & echo `$! > `"$pidFile`""
    & bash -lc $bashCmd
    Write-Ok "Started in background, pid file=$pidFile, log=$logFile"
}

# --- main ---
$repoRoot = Get-RepoRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "publish/prod"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

if ($Runtime -eq "auto") {
    $Runtime = Get-DefaultRuntime
}

Write-Step "Repo: $repoRoot"
Write-Step "Output: $OutputDir"
Write-Step "Runtime: $Runtime (SelfContained=$SelfContained)"

if ($Stop) {
    Stop-MezubeProcess -PublishDir $OutputDir -Name $ServiceName
    Write-Ok "Stop requested completed"
    if (-not $Run -and -not $Start -and -not $InstallService -and $SkipPublish) {
        return
    }
}

if (-not $SkipChecks) {
    Write-Step "Checking prerequisites"
    Assert-Command "dotnet" "Install .NET 10 SDK: https://dotnet.microsoft.com/download"
    $sdkLines = & dotnet --list-sdks
    if (-not ($sdkLines | Where-Object { $_ -match '^10\.' })) {
        Write-WarnMsg "No .NET 10 SDK listed. Publish may still work with a newer roll-forward SDK."
    }

    if (-not $SelfContained) {
        $runtimes = & dotnet --list-runtimes
        if (-not ($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 10\.' })) {
            Write-WarnMsg "No .NET 10 runtime found. Use -SelfContained or install .NET 10 runtime on the host."
        }
    }

    foreach ($tool in @("ffmpeg", "yt-dlp")) {
        if (Test-Command $tool) {
            Write-Ok "$tool found"
        }
        else {
            Write-WarnMsg "$tool not on PATH (required at runtime for YouTube/audio)"
        }
    }
}

$dataDir = Join-Path $OutputDir "data"
$tempDir = Join-Path $OutputDir "temp"
$backupData = $null
if ((Test-Path $dataDir) -and -not $SkipPublish) {
    $backupData = Join-Path ([System.IO.Path]::GetTempPath()) ("mezube-data-" + [guid]::NewGuid().ToString("N"))
    Write-Step "Backing up data -> $backupData"
    Copy-Item -Recurse -Force $dataDir $backupData
}

if (-not $SkipPublish) {
    if ($Stop -or $Start -or $Run -or $InstallService) {
        Stop-MezubeProcess -PublishDir $OutputDir -Name $ServiceName
    }

    Write-Step "Publishing ($Configuration / $Runtime)"
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    $publishArgs = @(
        "publish", "Mezube.csproj",
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $OutputDir,
        "--nologo",
        "-p:PublishTrimmed=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false"
    )
    if ($SelfContained) {
        $publishArgs += @("--self-contained", "true", "-p:PublishSingleFile=false")
    }
    else {
        $publishArgs += @("--self-contained", "false")
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
    Write-Ok "Publish completed"
}

New-Item -ItemType Directory -Force -Path $dataDir, $tempDir | Out-Null
if ($backupData -and (Test-Path $backupData)) {
    Write-Step "Restoring data"
    Copy-Item -Recurse -Force (Join-Path $backupData "*") $dataDir
    Remove-Item -Recurse -Force $backupData -ErrorAction SilentlyContinue
}

foreach ($localName in @("appsettings.prod.local.json", "appsettings.local.json")) {
    $src = Join-Path $repoRoot $localName
    $dst = Join-Path $OutputDir $localName
    if ((Test-Path $src) -and -not (Test-Path $dst)) {
        Copy-Item $src $dst
        Write-Ok "Copied $localName into publish dir"
    }
}

New-RunScripts -PublishDir $OutputDir -Rid $Runtime -IsSelfContainedBuild:([bool]$SelfContained)
$unitPath = New-SystemdUnit -PublishDir $OutputDir -Name $ServiceName -Rid $Runtime -IsSelfContainedBuild:([bool]$SelfContained)
Write-Ok "Wrote run.ps1 / run.sh / $(Split-Path $unitPath -Leaf)"

$startHint = if (Test-IsWindowsOS) {
    "pwsh `"$(Join-Path $OutputDir 'run.ps1')`""
}
else {
    "`"$(Join-Path $OutputDir 'run.sh')`""
}

$readme = @"
Mezube production publish
=========================
Runtime: $Runtime
SelfContained: $SelfContained
DOTNET_ENVIRONMENT: prod

Start (foreground):
  Windows:  pwsh ./run.ps1
  Linux:    ./run.sh

Start (background via deploy script):
  ./scripts/deploy-prod.ps1 -SkipPublish -Start
  ./scripts/deploy-prod.ps1 -SkipPublish -InstallService -Start   # Linux systemd

Secrets:
  Prefer appsettings.prod.local.json next to Mezube.dll (gitignored).
  Or set env vars: Mezon__BotId, Mezon__Token, Mezon__ServerKey, ...

Data:
  SQLite: ./data/tracks.db
  Temp:   ./temp/
"@
Write-Utf8File -Path (Join-Path $OutputDir "DEPLOY.txt") -Content $readme

if ($InstallService) {
    Install-SystemdService -UnitPath $unitPath -Name $ServiceName
}

if ($Start) {
    Start-Mezube -PublishDir $OutputDir -Name $ServiceName -AsService:([bool]$InstallService)
}

if ($Run) {
    Write-Step "Running Mezube in foreground"
    Set-Location $OutputDir
    $env:DOTNET_ENVIRONMENT = "prod"
    if (Test-IsWindowsOS) {
        & (Join-Path $OutputDir "run.ps1")
    }
    else {
        & bash (Join-Path $OutputDir "run.sh")
    }
}

Write-Host ""
Write-Ok "Deploy ready: $OutputDir"
Write-Host "Start: $startHint"
