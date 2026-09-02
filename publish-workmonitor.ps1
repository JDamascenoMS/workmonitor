param(
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "artifacts\workmonitor"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $root $OutputDirectory
if ([string]::IsNullOrWhiteSpace($Runtime) -or
    [System.IO.Path]::GetFileName($Runtime) -ne $Runtime) {
    throw "Runtime must be a single runtime identifier such as win-x64."
}
$runtimeOutput = Join-Path $output $Runtime
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) {
    $dotnetPath = $dotnetCommand.Source
}
else {
    $localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $localDotnet)) {
        throw ".NET 8 SDK was not found."
    }
    $dotnetPath = $localDotnet
}

New-Item -ItemType Directory -Path $runtimeOutput -Force | Out-Null

function Publish-Host {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Project,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $target = Join-Path $runtimeOutput $Name
    $suffix = [Guid]::NewGuid().ToString("N")
    $staging = Join-Path $runtimeOutput "$Name.staging.$suffix"
    $previous = Join-Path $runtimeOutput "$Name.previous.$suffix"

    & $dotnetPath publish $Project `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $staging `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "$Name publish failed with exit code $LASTEXITCODE."
    }

    if (Test-Path -LiteralPath $target) {
        Move-Item -LiteralPath $target -Destination $previous
    }

    try {
        Move-Item -LiteralPath $staging -Destination $target
    }
    catch {
        if (Test-Path -LiteralPath $previous) {
            Move-Item -LiteralPath $previous -Destination $target
        }
        throw
    }

    if (Test-Path -LiteralPath $previous) {
        try {
            Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Previous $Name package remains at $previous until its running process exits."
        }
    }
}

Publish-Host `
    -Project (Join-Path $root "src\WorkMonitor.Cli\WorkMonitor.Cli.csproj") `
    -Name "cli"

Publish-Host `
    -Project (Join-Path $root "src\WorkMonitor.Mcp\WorkMonitor.Mcp.csproj") `
    -Name "mcp"

Write-Host "Published WorkMonitor to $runtimeOutput"
