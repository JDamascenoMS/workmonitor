<#
.SYNOPSIS
    Updates FT-MONITOR from git, builds, runs, and opens the home page.

.DESCRIPTION
    One-click "desktop-app" style launcher:
      1. git fetch + pull (fast-forward) the current branch.
      2. dotnet restore + build (Release by default).
      3. Launch the app (dotnet run) listening on https://localhost:7286.
      4. Wait until the site responds, then open the index in the default browser.

    Git authentication uses whatever credential helper is already configured
    on the machine (Git Credential Manager, cached SSH key, PAT, etc.). If
    pulling requires credentials and none are cached, git will prompt in the
    console window the script is running in.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER NoPull
    Skip the git fetch/pull step.

.PARAMETER NoBuild
    Skip the build step.

.PARAMETER Url
    The URL to open in the browser once the app is up.
    Default: https://localhost:7286/

.EXAMPLE
    ./launch-ft-monitor.ps1

.EXAMPLE
    ./launch-ft-monitor.ps1 -NoPull -Configuration Debug
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoPull,
    [switch]$NoBuild,
    [string]$Url = 'https://localhost:7286/'
)

$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------------------
# Resolve repo + project paths relative to this script's location
# --------------------------------------------------------------------------
$RepoRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir  = Join-Path $RepoRoot 'FT-MONITOR'
$ProjectFile = Join-Path $ProjectDir 'FT-MONITOR.csproj'

if (-not (Test-Path $ProjectFile)) {
    Write-Error "Cannot find $ProjectFile. Place this script at the repo root."
}

function Write-Step($msg) {
    Write-Host ''
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Invoke-Native($file, [string[]]$arguments, [string]$workingDir) {
    $argLine = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    Write-Host "    $file $argLine" -ForegroundColor DarkGray
    $p = Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $workingDir `
        -NoNewWindow -PassThru -Wait
    if ($p.ExitCode -ne 0) {
        throw "'$file $argLine' exited with code $($p.ExitCode)."
    }
}

# --------------------------------------------------------------------------
# 1) Git update
# --------------------------------------------------------------------------
if (-not $NoPull) {
    Write-Step 'Updating from git'
    try {
        Invoke-Native 'git' @('fetch', '--all', '--prune') $RepoRoot

        $branch = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD).Trim()
        Write-Host "    Current branch: $branch" -ForegroundColor DarkGray

        # Detect a dirty working tree — we never overwrite local edits.
        $status = (& git -C $RepoRoot status --porcelain)
        if ($status) {
            Write-Warning "Working tree has uncommitted changes; skipping 'git pull'. Use git to stash/commit first."
        }
        else {
            Invoke-Native 'git' @('pull', '--ff-only', 'origin', $branch) $RepoRoot
        }
    }
    catch {
        Write-Warning "Git update failed: $($_.Exception.Message)"
        Write-Warning 'Continuing with the local copy.'
    }
}
else {
    Write-Step 'Skipping git update (-NoPull)'
}

# --------------------------------------------------------------------------
# 2) Build
# --------------------------------------------------------------------------
if (-not $NoBuild) {
    Write-Step "Building ($Configuration)"
    Invoke-Native 'dotnet' @('build', $ProjectFile, '-c', $Configuration, '--nologo') $RepoRoot
}
else {
    Write-Step 'Skipping build (-NoBuild)'
}

# --------------------------------------------------------------------------
# 3) Launch the app
# --------------------------------------------------------------------------
Write-Step 'Launching FT-MONITOR'

# Run the app in a separate PowerShell window so its console stays open and the
# user can see logs / Ctrl+C to shut it down. The window is tied to this script
# only by the user's desire to keep the app running.
$runArgs = @(
    '-NoExit',
    '-ExecutionPolicy', 'Bypass',
    '-Command',
    "Set-Location `"$ProjectDir`"; `$env:ASPNETCORE_ENVIRONMENT='Development'; dotnet run --no-build -c $Configuration --launch-profile https"
)
$app = Start-Process -FilePath 'pwsh.exe' -ArgumentList $runArgs -WorkingDirectory $ProjectDir -PassThru -ErrorAction SilentlyContinue
if (-not $app) {
    # pwsh not on PATH — fall back to Windows PowerShell.
    $app = Start-Process -FilePath 'powershell.exe' -ArgumentList $runArgs -WorkingDirectory $ProjectDir -PassThru
}

Write-Host "    App process started (PID $($app.Id))." -ForegroundColor DarkGray

# --------------------------------------------------------------------------
# 4) Wait for the site to respond, then open the browser
# --------------------------------------------------------------------------
Write-Step "Waiting for $Url to come online"

# Trust the dev cert for the duration of the probe so Invoke-WebRequest
# doesn't fail with an SSL error during startup polling. Done in pure
# PowerShell (no Add-Type) so re-runs in the same session don't choke on
# "type already exists" compilation errors.
try {
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
} catch {
    Write-Warning "Could not relax cert validation: $($_.Exception.Message)"
}

$deadline = (Get-Date).AddSeconds(90)
$ready = $false

# -SkipCertificateCheck only exists on PowerShell 7+; conditionally include it.
$iwrSupportsSkipCert = (Get-Command Invoke-WebRequest).Parameters.ContainsKey('SkipCertificateCheck')

while ((Get-Date) -lt $deadline) {
    try {
        if ($iwrSupportsSkipCert) {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3 -SkipCertificateCheck -ErrorAction Stop
        } else {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        }
        if ($r.StatusCode -ge 200 -and $r.StatusCode -lt 500) {
            $ready = $true
            break
        }
    }
    catch {
        Start-Sleep -Milliseconds 750
    }
}

if (-not $ready) {
    Write-Warning "App did not respond at $Url within 90s. Opening browser anyway."
}
else {
    Write-Host "    Site is up." -ForegroundColor Green
}

Write-Step "Opening $Url"
# Start-Process $Url is unreliable under pwsh.exe (PowerShell 7) — it tries
# to launch the URL as if it were an executable instead of using the shell's
# URL association. Use ShellExecute explicitly, with cmd /c start as a fallback.
$opened = $false
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Url
    $psi.UseShellExecute = $true
    [System.Diagnostics.Process]::Start($psi) | Out-Null
    $opened = $true
}
catch {
    Write-Warning "ShellExecute failed: $($_.Exception.Message)"
}

if (-not $opened) {
    try {
        # The empty "" is the window title required by cmd's start command.
        Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', 'start', '""', $Url) -WindowStyle Hidden | Out-Null
        $opened = $true
    }
    catch {
        Write-Warning "cmd /c start failed: $($_.Exception.Message)"
    }
}

if (-not $opened) {
    Write-Warning "Could not auto-open the browser. Open this URL manually: $Url"
}

Write-Host ''
Write-Host 'FT-MONITOR is running. Close the app window (or press Ctrl+C in it) to stop the server.' -ForegroundColor Green
