# FT-MONITOR

Internal feedback / bug / GitHub-issues triage dashboard for the DevDiv tooling
team. ASP.NET Core (.NET 8) app with Microsoft Entra sign-in, Azure DevOps
queries, Azure OpenAI summarization, and GitHub Issues integration.

## WorkMonitor conversational CLI

The repository now includes a reusable agent stack and two additional hosts:

- `src/WorkMonitor.Core` contains the Azure DevOps and GitHub integrations.
- `src/WorkMonitor.Agent` contains the streaming conversation runtime and tools.
- `src/WorkMonitor.Cli` provides a resumable terminal conversation.
- `src/WorkMonitor.Mcp` exposes the same tools over the official MCP stdio SDK.

Configure the model using environment variables or the existing .NET user-secrets id:

```powershell
$env:AzureOpenAI__Endpoint = "https://YOUR-RESOURCE.cognitiveservices.azure.com/"
$env:AzureOpenAI__DeploymentName = "YOUR-DEPLOYMENT"

# Optional. When omitted, Azure identity authentication is used.
$env:AzureOpenAI__ApiKey = "YOUR-KEY"
```

For the CLI, pin the tenant and expected account, then authenticate once:

```powershell
$env:WorkMonitor__Authentication__TenantId = "YOUR-TENANT-ID"
$env:WorkMonitor__Authentication__LoginHint = "you@example.com"

dotnet run --project src/WorkMonitor.Cli -- --auth-login
dotnet run --project src/WorkMonitor.Cli -- --check
```

The CLI and MCP host share one encrypted persistent token cache for Azure OpenAI
and Azure DevOps. The non-secret account record is stored beside the session
database in `%LOCALAPPDATA%\WorkMonitor`. Use `--auth-reset` to clear the saved
account selection and authenticate again.

Run the CLI:

```powershell
dotnet run --project src/WorkMonitor.Cli
```

Useful commands include `/new`, `/sessions`, `/resume`, `/model`, `/tools`,
`/working-set`, `/dry-run`, `/live`, `/audit`, and `/exit`. Conversations and
write audit entries are stored in `%LOCALAPPDATA%\WorkMonitor\workmonitor.db`.
Live writes require confirmation for every operation.

Run the MCP stdio host:

```powershell
dotnet run --project src/WorkMonitor.Mcp
```

MCP writes are dry-run by default. To allow them, set
`WorkMonitor__Mcp__AllowWrites=true`; each write call must still pass
`confirm=true`.

Publish self-contained Windows executables:

```powershell
./publish-workmonitor.ps1
```

The CLI and MCP executables are written under
`artifacts/workmonitor/<runtime>`, for example
`artifacts/workmonitor/win-x64`.

### Azure OpenAI authorization

When `AzureOpenAI:ApiKey` is empty, the CLI uses its tenant-pinned persistent
interactive credential. The web host continues to use `DefaultAzureCredential`
so it can run under managed identity. The selected identity must have the
`Cognitive Services OpenAI User` role on the Azure OpenAI resource that owns
the deployment. A `401 PermissionDenied` response means that role is missing,
the wrong identity was selected, or the configured endpoint belongs to a
different resource. The endpoint must be the Azure OpenAI resource endpoint,
not a Foundry project-management endpoint. Run `--check` to see the saved
identity, or `--auth-reset` followed by `--auth-login` to select it again.

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
