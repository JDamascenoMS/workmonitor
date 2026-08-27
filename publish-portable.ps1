<#
.SYNOPSIS
	Produces a self-contained, single-file portable build of FT-MONITOR
	that can be zipped and shared.

.DESCRIPTION
	Publishes the app as a Windows x64 self-contained single-file executable
	(no .NET runtime required on the target machine), then drops a small
	`Run-FT-MONITOR.cmd` launcher next to it that starts the app and opens
	the home page in the default browser.

	The published folder is fully portable — zip it, send it, double-click
	`Run-FT-MONITOR.cmd` to run.

	NOTE: appsettings.json IS included in the publish output. Any secrets
	placed there (GitHub PAT, Azure OpenAI key, etc.) will be shipped to
	whoever receives the zip. Use user-secrets / env vars in development
	and re-add real secrets in the published copy before sharing.

.PARAMETER OutputDir
	Where to place the published files. Default: ./publish

.PARAMETER Runtime
	Target runtime identifier. Default: win-x64

.PARAMETER Configuration
	Build configuration. Default: Release

.EXAMPLE
	./publish-portable.ps1
	Compress-Archive -Path ./publish/* -DestinationPath FT-MONITOR-portable.zip
#>

[CmdletBinding()]
param(
	[string]$OutputDir    = './publish',
	[string]$Runtime      = 'win-x64',
	[string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$RepoRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectFile = Join-Path $RepoRoot 'FT-MONITOR/FT-MONITOR.csproj'
$Resolved    = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
	$OutputDir
} else {
	Join-Path $RepoRoot $OutputDir
}

if (-not (Test-Path $ProjectFile)) {
	throw "Cannot find $ProjectFile."
}

Write-Host "==> Cleaning $Resolved" -ForegroundColor Cyan
if (Test-Path $Resolved) {
	Remove-Item -Recurse -Force $Resolved
}
New-Item -ItemType Directory -Path $Resolved | Out-Null

Write-Host "==> Publishing FT-MONITOR ($Configuration, $Runtime, self-contained, single-file)" -ForegroundColor Cyan
& dotnet publish $ProjectFile `
	-c $Configuration `
	-r $Runtime `
	--self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:EnableCompressionInSingleFile=true `
	-p:DebugType=None `
	-p:DebugSymbols=false `
	-o $Resolved
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# --------------------------------------------------------------------------
# Drop a portable launcher next to the EXE
# --------------------------------------------------------------------------
$launcherPath = Join-Path $Resolved 'Run-FT-MONITOR.cmd'

# Use a here-string with the URL hard-coded; the published app's launchSettings
# is ignored at runtime, so we set the ASPNETCORE_URLS env var explicitly.
$launcherBody = @'
@echo off
setlocal
title FT-MONITOR

REM Bind to a fixed local port so the launcher knows where to point the browser.
set ASPNETCORE_URLS=https://localhost:7286;http://localhost:5191
set ASPNETCORE_ENVIRONMENT=Production

REM Start the app in this window. Closing the window stops the server.
start "" "" "https://localhost:7286/"
"%~dp0FT-MONITOR.exe"

endlocal
'@

Set-Content -Path $launcherPath -Value $launcherBody -Encoding ASCII

# A readme to set expectations for whoever receives the zip.
$readmePath = Join-Path $Resolved 'README.txt'
$readmeBody = @"
FT-MONITOR — portable build
============================

To run:
  1. Double-click Run-FT-MONITOR.cmd.
  2. A console window opens (the running server). Your default browser
	 will navigate to https://localhost:7286/.
  3. On first run Windows may warn about the dev HTTPS certificate.
	 If so, run:  dotnet dev-certs https --trust
	 (Requires the .NET SDK; alternatively just click through the warning.)

To stop:
  Close the console window, or press Ctrl+C in it.

What's inside:
  FT-MONITOR.exe        Self-contained single-file app (no .NET install needed).
  Run-FT-MONITOR.cmd    Convenience launcher (sets URL, opens browser).
  appsettings.json      Configuration (Entra auth, GitHub repos, etc.).
  wwwroot/              Static UI assets.

Built on:  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
Runtime:   $Runtime
"@

Set-Content -Path $readmePath -Value $readmeBody -Encoding UTF8

Write-Host ''
Write-Host "==> Done. Portable build at:" -ForegroundColor Green
Write-Host "    $Resolved"
Write-Host ''
Write-Host "Next: zip it up and share." -ForegroundColor Green
Write-Host "    Compress-Archive -Path `"$Resolved\*`" -DestinationPath FT-MONITOR-portable.zip -Force"
