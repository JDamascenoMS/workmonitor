<#
.SYNOPSIS
    Deploys FT-MONITOR to Azure App Service (FT-MONITOR20260323).

.DESCRIPTION
    1. Publishes the .NET 8 app.
    2. Deploys to the existing App Service using az webapp deploy.

.PARAMETER ResourceGroup
    Resource group name (default: wte-nsp-rg).

.PARAMETER AppName
    App Service name (default: FT-MONITOR20260323).
#>
param(
    [string]$ResourceGroup = "wte-nsp-rg",
    [string]$AppName = "FT-MONITOR20260323"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " FT-MONITOR App Service Deployment" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ------------------------------------------------------------------
# Step 1 - Ensure logged in
# ------------------------------------------------------------------
Write-Host "`n[1/3] Checking Azure CLI login..." -ForegroundColor Yellow
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not logged in. Running az login..." -ForegroundColor Yellow
    az login
}
Write-Host "Subscription: $($account.name) ($($account.id))" -ForegroundColor Green

# ------------------------------------------------------------------
# Step 2 - Publish the .NET app and create zip
# ------------------------------------------------------------------
Write-Host "`n[2/3] Publishing FT-MONITOR .NET 8 app..." -ForegroundColor Yellow
$publishDir = "publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish FT-MONITOR/FT-MONITOR.csproj -c Release -o $publishDir --nologo

$zipPath = "publish.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -Force
Write-Host "Published and zipped to ./$zipPath" -ForegroundColor Green

# ------------------------------------------------------------------
# Step 3 - Deploy to App Service
# ------------------------------------------------------------------
Write-Host "`n[3/3] Deploying to App Service '$AppName'..." -ForegroundColor Yellow

az webapp deploy `
    --resource-group $ResourceGroup `
    --name $AppName `
    --src-path $zipPath `
    --type zip `
    --output none

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Deployment complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  App: https://$AppName.azurewebsites.net" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Managed Identity Principal ID: a62b2c2a-4be9-4d38-ad49-c6573f5e8763" -ForegroundColor Gray
Write-Host "  (already enabled, federated credential should be configured)" -ForegroundColor Gray
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
