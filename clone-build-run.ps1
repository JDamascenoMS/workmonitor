<#
.SYNOPSIS
    Clones a repository, builds the FT-MONITOR solution, and runs the app.

.DESCRIPTION
    This script performs the local developer bootstrap flow for FT-MONITOR:
    1. Clones the repository into a target folder.
    2. Restores and builds the solution.
    3. Runs the ASP.NET Core app from the FT-MONITOR project.

.PARAMETER RepoUrl
    Git repository URL or local git path to clone.

.PARAMETER DestinationPath
    Folder where the repository should be cloned. If omitted, the folder name
    is derived from the repository URL.

.PARAMETER Branch
    Optional branch to clone.

.PARAMETER Configuration
    Build configuration. Defaults to Debug.

.PARAMETER SolutionPath
    Relative path to the solution inside the cloned repository.

.PARAMETER ProjectPath
    Relative path to the runnable project inside the cloned repository.

.PARAMETER EnvironmentName
    Value assigned to ASPNETCORE_ENVIRONMENT before running the app.

.EXAMPLE
    .\clone-build-run.ps1 -RepoUrl "https://github.com/contoso/FT-MONITOR.git"

.EXAMPLE
    .\clone-build-run.ps1 -RepoUrl "https://github.com/contoso/FT-MONITOR.git" -Branch main -DestinationPath "C:\dev\FT-MONITOR"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$RepoUrl,

    [string]$DestinationPath,

    [string]$Branch,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$SolutionPath = "FT-MONITOR.sln",

    [string]$ProjectPath = "FT-MONITOR/FT-MONITOR.csproj",

    [string]$EnvironmentName = "Development"
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param(
        [string]$Message
    )

    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Assert-CommandAvailable {
    param(
        [string]$CommandName
    )

    if (-not (Get-Command $CommandName -ErrorAction SilentlyContinue)) {
        throw "Required command '$CommandName' was not found in PATH."
    }
}

function Get-DefaultDestinationPath {
    param(
        [string]$Source
    )

    $trimmedSource = $Source.TrimEnd('/', '\')
    $leaf = Split-Path -Path $trimmedSource -Leaf

    if ($leaf.EndsWith('.git', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $leaf.Substring(0, $leaf.Length - 4)
    }

    return $leaf
}

Assert-CommandAvailable -CommandName "git"
Assert-CommandAvailable -CommandName "dotnet"

if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $DestinationPath = Join-Path -Path (Get-Location) -ChildPath (Get-DefaultDestinationPath -Source $RepoUrl)
}

$resolvedDestinationParent = Split-Path -Path $DestinationPath -Parent
if ([string]::IsNullOrWhiteSpace($resolvedDestinationParent)) {
    $resolvedDestinationParent = Get-Location
}

if (-not (Test-Path $resolvedDestinationParent)) {
    New-Item -ItemType Directory -Path $resolvedDestinationParent | Out-Null
}

if (Test-Path $DestinationPath) {
    $existingEntries = Get-ChildItem -Path $DestinationPath -Force -ErrorAction SilentlyContinue
    if ($existingEntries) {
        throw "Destination path '$DestinationPath' already exists and is not empty."
    }
}

$cloneArgs = @("clone")
if (-not [string]::IsNullOrWhiteSpace($Branch)) {
    $cloneArgs += @("--branch", $Branch)
}
$cloneArgs += @($RepoUrl, $DestinationPath)

Write-Step -Message "Cloning repository into '$DestinationPath'"
& git @cloneArgs
if ($LASTEXITCODE -ne 0) {
    throw "git clone failed with exit code $LASTEXITCODE."
}

Push-Location $DestinationPath
try {
    if (-not (Test-Path $SolutionPath)) {
        throw "Solution file '$SolutionPath' was not found in the cloned repository."
    }

    if (-not (Test-Path $ProjectPath)) {
        throw "Project file '$ProjectPath' was not found in the cloned repository."
    }

    Write-Step -Message "Restoring solution '$SolutionPath'"
    & dotnet restore $SolutionPath --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    Write-Step -Message "Building solution '$SolutionPath' ($Configuration)"
    & dotnet build $SolutionPath --configuration $Configuration --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $env:ASPNETCORE_ENVIRONMENT = $EnvironmentName
    Write-Step -Message "Running app from '$ProjectPath' with ASPNETCORE_ENVIRONMENT=$EnvironmentName"
    & dotnet run --project $ProjectPath --configuration $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet run failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}