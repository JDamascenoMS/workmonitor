# FT-MONITOR

Internal feedback / bug / GitHub-issues triage dashboard for the DevDiv tooling
team. ASP.NET Core (.NET 8) app with Microsoft Entra sign-in, Azure DevOps
queries, Azure OpenAI summarization, and GitHub Issues integration.

## Run it locally

```powershell
# from the repo root
dotnet build FT-MONITOR/FT-MONITOR.csproj
dotnet run    --project FT-MONITOR
```

Or just double-click `FT-MONITOR.cmd` at the repo root — it git-pulls, builds,
runs, and opens https://localhost:7286/ in your browser.

## Share a portable build

```powershell
./publish-portable.ps1
Compress-Archive -Path ./publish/* -DestinationPath FT-MONITOR-portable.zip -Force
```

Send the zip. Recipient unzips, double-clicks `Run-FT-MONITOR.cmd`. No .NET
SDK required on the target machine (the .NET runtime is bundled inside the
single-file `FT-MONITOR.exe`).

⚠️ Before zipping, decide whether you want to embed secrets in
`publish/appsettings.json`. By default it ships with empty placeholders.

## Secrets configuration

`appsettings.json` ships with **empty placeholders** for all secrets — never
commit real values to it. Use one of these per-machine mechanisms instead:

### Option 1 — dotnet user-secrets (recommended for dev)

```powershell
cd FT-MONITOR

# Azure OpenAI
dotnet user-secrets set "AzureOpenAI:Endpoint"       "https://YOUR-RESOURCE.cognitiveservices.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey"         "YOUR-KEY"
dotnet user-secrets set "AzureOpenAI:DeploymentName" "gpt-5.2-chat"

# GitHub PAT (raises GitHub API limit from 60/hr to 5000/hr)
dotnet user-secrets set "GitHub:Token" "ghp_xxxxxxxxxxxx"
```

User-secrets are stored in `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`
on Windows. They never touch the repo.

### Option 2 — environment variables

ASP.NET Core configuration uses `__` as the separator for nested keys.

```powershell
$env:AzureOpenAI__ApiKey         = 'YOUR-KEY'
$env:AzureOpenAI__Endpoint       = 'https://YOUR-RESOURCE.cognitiveservices.azure.com/'
$env:AzureOpenAI__DeploymentName = 'gpt-5.2-chat'
$env:GitHub__Token               = 'ghp_xxxxxxxxxxxx'
```

### Option 3 — Azure Key Vault (production)

Wire up `AddAzureKeyVault(...)` in `Program.cs` and reference the same keys.

## Other prerequisites

- .NET 8 SDK.
- An account in the configured Entra tenant (see `AzureAd` section in
  `appsettings.json`).
- Azure CLI signed in (`az login`) so `DefaultAzureCredential` can mint
  Azure DevOps tokens for the app.
- For the dev HTTPS cert: `dotnet dev-certs https --trust` (once per machine).

## Project layout

```
FT-MONITOR/
  Clients/      Raw API clients (Azure DevOps, GitHub).
  Controllers/  ASP.NET MVC API controllers.
  Services/     Business-logic services (AI, tokens, parameters, monitoring).
  wwwroot/      Static dashboard pages (HTML + JS, no framework).
launch-ft-monitor.ps1   Dev-machine launcher (git pull -> build -> run -> open).
publish-portable.ps1    Produces a single-file portable build.
FT-MONITOR.cmd          CMD wrapper around launch-ft-monitor.ps1.
```
