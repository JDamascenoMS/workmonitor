<#
.SYNOPSIS
    Deploys FT-MONITOR to an Azure DevTest Labs VM.

.DESCRIPTION
    1. Creates the resource group and DevTest Lab + VM via Bicep.
    2. Publishes the .NET 8 app.
    3. Copies the published artifacts to the VM and configures IIS.

.PARAMETER AdminPassword
    Password for the VM admin account (ftadmin).

.PARAMETER ResourceGroup
    Resource group name (default: ft-monitor-rg).

.PARAMETER Location
    Azure region (default: eastus2).
#>
param(
    [Parameter(Mandatory)]
    [string]$AdminPassword,

    [string]$ResourceGroup = "ft-monitor-rg",
    [string]$Location = "eastus2"
)

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " FT-MONITOR DevTest Labs Deployment" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ------------------------------------------------------------------
# Step 1 - Ensure logged in
# ------------------------------------------------------------------
Write-Host "`n[1/6] Checking Azure CLI login..." -ForegroundColor Yellow
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Not logged in. Running az login..." -ForegroundColor Yellow
    az login
}
Write-Host "Subscription: $($account.name) ($($account.id))" -ForegroundColor Green

# ------------------------------------------------------------------
# Step 2 - Create Resource Group
# ------------------------------------------------------------------
Write-Host "`n[2/6] Creating resource group '$ResourceGroup' in '$Location'..." -ForegroundColor Yellow
az group create --name $ResourceGroup --location $Location --output none

# ------------------------------------------------------------------
# Step 3 - Deploy Bicep (Lab + VM)
# ------------------------------------------------------------------
Write-Host "`n[3/6] Deploying DevTest Lab and VM via Bicep..." -ForegroundColor Yellow
Write-Host "       This may take 10-15 minutes for VM provisioning." -ForegroundColor Gray

az deployment group create `
    --resource-group $ResourceGroup `
    --template-file infra/main.bicep `
    --parameters infra/main.bicepparam `
    --parameters adminPassword=$AdminPassword `
    --output none

Write-Host "Lab and VM deployed successfully." -ForegroundColor Green

# ------------------------------------------------------------------
# Step 4 - Publish the .NET app
# ------------------------------------------------------------------
Write-Host "`n[4/6] Publishing FT-MONITOR .NET 8 app..." -ForegroundColor Yellow
$publishDir = "publish"
dotnet publish FT-MONITOR/FT-MONITOR.csproj -c Release -o $publishDir --nologo

Write-Host "Published to ./$publishDir" -ForegroundColor Green

# ------------------------------------------------------------------
# Step 5 - Get VM public IP and enable Managed Identity
# ------------------------------------------------------------------
Write-Host "`n[5/7] Retrieving VM connection details..." -ForegroundColor Yellow

$vmInfo = az lab vm show `
    --resource-group $ResourceGroup `
    --lab-name ft-monitor-lab `
    --name ft-monitor-vm `
    --output json | ConvertFrom-Json

$computeRg = ($vmInfo.computeId -split '/')[4]
$computeVmName = ($vmInfo.computeId -split '/')[-1]

$publicIp = az vm list-ip-addresses `
    --resource-group $computeRg `
    --name $computeVmName `
    --query "[0].virtualMachine.network.publicIpAddresses[0].ipAddress" `
    --output tsv

Write-Host "VM Public IP: $publicIp" -ForegroundColor Green

# ------------------------------------------------------------------
# Step 6 - Enable system-assigned Managed Identity on the compute VM
# ------------------------------------------------------------------
Write-Host "`n[6/7] Enabling system-assigned Managed Identity on VM..." -ForegroundColor Yellow

$identityResult = az vm identity assign `
    --resource-group $computeRg `
    --name $computeVmName `
    --output json | ConvertFrom-Json

$principalId = $identityResult.systemAssignedIdentity
Write-Host "Managed Identity Principal ID: $principalId" -ForegroundColor Green

# ------------------------------------------------------------------
# Step 7 - Instructions for final deployment to VM
# ------------------------------------------------------------------
Write-Host "`n[7/7] Final setup instructions" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "The DevTest Lab VM is provisioned with Managed Identity." -ForegroundColor White
Write-Host ""
Write-Host "  REQUIRED: Create a federated credential on your app registration:" -ForegroundColor Red
Write-Host "    Entra ID > App registrations > Ft-Monitor-Entra > Certificates & secrets" -ForegroundColor Gray
Write-Host "    > Federated credentials > Add credential" -ForegroundColor Gray
Write-Host "      Scenario:            Other issuer" -ForegroundColor Green
Write-Host "      Issuer:              https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0" -ForegroundColor Green
Write-Host "      Subject identifier:  $principalId" -ForegroundColor Green
Write-Host "      Audience:            api://AzureADTokenExchange" -ForegroundColor Green
Write-Host ""
Write-Host "  1. RDP into the VM:" -ForegroundColor White
Write-Host "     mstsc /v:$publicIp" -ForegroundColor Green
Write-Host "     User: ftadmin  |  Password: (the one you provided)" -ForegroundColor Gray
Write-Host ""
Write-Host "  2. On the VM, run in PowerShell (Admin):" -ForegroundColor White
Write-Host "     # Install ASP.NET Core 8.0 Hosting Bundle" -ForegroundColor Gray
Write-Host '     Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile dotnet-install.ps1' -ForegroundColor Green
Write-Host '     winget install Microsoft.DotNet.HostingBundle.8 --accept-package-agreements --accept-source-agreements' -ForegroundColor Green
Write-Host ""
Write-Host "  3. Copy the '$publishDir' folder to the VM (e.g. C:\inetpub\ft-monitor)" -ForegroundColor White
Write-Host ""
Write-Host "  4. Configure IIS site on the VM:" -ForegroundColor White
Write-Host '     New-IISSite -Name "FT-MONITOR" -PhysicalPath "C:\inetpub\ft-monitor" -BindingInformation "*:80:"' -ForegroundColor Green
Write-Host ""
Write-Host "  Alternatively, run as a standalone Kestrel service:" -ForegroundColor White
Write-Host '     cd C:\inetpub\ft-monitor' -ForegroundColor Green
Write-Host '     .\FT-MONITOR.exe --urls "http://*:80"' -ForegroundColor Green
Write-Host ""
Write-Host "  App will be available at: http://$publicIp" -ForegroundColor Cyan
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Portal: https://portal.azure.com/#@/resource/subscriptions/$($account.id)/resourceGroups/$ResourceGroup" -ForegroundColor Gray
