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

Read-only agent capabilities include:

- Assigned and mentioned Azure DevOps work items
- Configured saved-query discovery and execution
- Azure DevOps pull requests awaiting the current user's review
- Recent assigned/mentioned work with newest comments
- Work-item details and text search
- GitHub issue listing and details
- Explicit-reference correlation between DevOps work items and GitHub issues

Recent activity and correlation results preserve their JSON structure under
an aggregate output-size budget. Partial-result flags expose truncated output
or candidate windows; use `get_work_item` or `get_github_issue` for additional
details. Correlation only follows explicit references in configured GitHub
repositories, and reports when its bounded DevOps comment scan is partial.

The CLI and MCP hosts use the persisted interactive user credential. The web
chat uses delegated Azure DevOps tokens for its signed-in user, so `@Me` is
not evaluated against a server or managed identity. Web chat exposes the
12 read-only tools; comment posting, field updates, and Bug creation are
intentionally unavailable there.

Run the MCP stdio host:

```powershell
dotnet run --project src/WorkMonitor.Mcp
```

MCP writes are dry-run by default. To allow them, set
`WorkMonitor__Mcp__AllowWrites=true`; each write call must still pass
`confirm=true`.

The CLI and MCP host expose 15 tools, including three write tools:
`add_work_item_comment`, `update_work_item`, and `create_bug_from_feedback`.
Field updates use this input
(MCP adds the optional `confirm` flag, which defaults to `false`):

```json
{
  "id": 12345,
  "expectedRevision": 7,
  "fields": {
    "title": "Clarify the failure message",
    "description": "<p>Updated reproduction steps.</p>",
    "state": "Active",
    "assignedTo": "engineer@example.com",
    "priority": 2,
    "tags": ["triage", "customer"]
  },
  "confirm": false
}
```

Call `get_work_item` first and copy its `revision` into `expectedRevision`.
Supply any nonempty subset of the six fields; omitted or `null` fields remain
unchanged. An empty description clears it, `assignedTo: ""` unassigns the item,
and `tags: []` clears all tags. A supplied tag list replaces the entire list
(individual tags cannot contain semicolons). Titles must be nonblank and at most
255 characters. Priorities are nonnegative integers whose allowed values depend
on the work item type/process, not a universal 1-to-4 scale: current DevDiv Bugs
allow 0, 1, 2, 3 and FeedbackTickets allow 0, 1, 2. Live updates defer these
type-specific constraints to Azure DevOps. State transitions and identities
also remain subject to process rules and the signed-in user's permissions.
Unknown fields and empty updates are rejected. This tool cannot create work
items or change relationships.

Dry runs preview the exact field values without authenticating or contacting
Azure DevOps; revision, identity, permission, and process validation happen on
the live PATCH. Live updates atomically test `/rev` before changing only the
requested fields. Stale revisions fail without an automatic retry: re-read the
item, review the new state, and explicitly approve a fresh update. CLI `/live`
requires per-operation approval and persists the write audit; MCP requires both
write enablement and `confirm=true`, using the same per-call tool audit as
comment posting. Once a write is dispatched, cancellation does not interrupt
observation of its outcome.

### Create an engineering Bug from feedback

`create_bug_from_feedback` creates a **Bug** with a
`System.LinkTypes.Related` link pointing from the new Bug to the source
**FeedbackTicket**. The relation is included in the same creation POST, not
added afterward; a rejected link cannot leave an unlinked Bug. It does not
separately edit feedback state, assignment, or comments.

```json
{
  "feedbackTicketId": 12345,
  "bug": {
    "title": "Project reload fails after a configuration change",
    "areaPath": "DevDiv\\NET Tools\\Project",
    "reproSteps": "<p>Steps: ...</p><p>Expected: ...</p><p>Actual: ...</p>",
    "priority": 0,
    "tags": ["triage"]
  },
  "confirm": false
}
```

`title`, `areaPath`, and `reproSteps` are required. Always choose the explicit
engineering area; do not blindly copy a FeedbackTicket's routing area.
DevDiv engineering Bugs use `Microsoft.VSTS.TCM.ReproSteps`; optional
`description` writes the separate `System.Description` field. Optional
`iterationPath`, `assignedTo`, `priority`, `tags`, and `source` are allowlisted.
`source` is the explicit `Microsoft.DevDiv.Source` category, not the feedback
id; omit it rather than inferring a customer category. State and unspecified
fields use process defaults (currently `Active` state and `PU/Feature team`
Source). Required server-derived Area/Iteration IDs are not supplied manually.

Dry runs preview fields and the intended relation without authenticating or
contacting Azure DevOps; they do **not** claim that the source or process has
been verified. A live call checks the source id and `FeedbackTicket` type,
reads current Bug field metadata, and validates the exact creation/link payload
using `validateOnly=true` before creating it. Priority and Source choices are
checked against metadata; conditional/required fields, area/iteration paths,
identity, link rules, and permissions remain enforced by Azure DevOps.
Validation failures expose the server error without bypassing rules.

Creation is non-idempotent. There is exactly one creation attempt and **no
automatic create retry**, including after timeouts, transport/server errors,
invalid receipts, or failed verification. After creation, the tool reads the
new work item back and verifies its id, Bug type, API URL, and actual Related
link before reporting success with the Bug id, browser/API URLs, and verified
source relation. An uncertain outcome is an error, with the reported Bug id/URL
when known: search for and verify that Bug and its link before considering
another explicitly approved attempt. There is no repair/link/comment fallback.
The shared agent also blocks another live creation for that source within the
same run after an uncertain outcome. The same CLI approval/audit and MCP dual
write gates apply.

Publish self-contained Windows executables:

```powershell
./publish-workmonitor.ps1
```

The CLI and MCP executables are written under
`artifacts/workmonitor/<runtime>`, for example
`artifacts/workmonitor/win-x64`.

### Create a release

Tags matching `v*` trigger the release workflow. Use a semantic version:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The workflow runs the complete build and test suite, publishes versioned
`win-x64` and `linux-x64` binaries, creates separate CLI and MCP archives,
generates `SHA256SUMS.txt`, and attaches everything to a GitHub Release.
Release archives contain only the self-contained executable and the safe
placeholder `appsettings.json`; account records, token caches, databases, and
user-secrets remain on the local machine. Configure a repository ruleset for
`v*` tags that blocks updates and deletion so published release tags remain
immutable.

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

### Read-only web chat milestone

Open `https://localhost:7286/agent.html`. Conversations are persisted and
isolated by the signed-in user's Entra tenant and object ID. Responses and
tool progress stream over authenticated NDJSON. Cancelling a turn preserves
the conversation. The browser uses MSAL Browser authorization code flow with
PKCE. No Entra client secret, application certificate, or server-side cookie
sign-in is required. The Azure OpenAI API key still authenticates the model
only and must remain on the server.
The supported MSAL Browser 5.25.0 bundle and callback bridge are served locally;
their MIT license, pinned SHA-256 provenance and integrity attributes are
included under `FT-MONITOR/wwwroot/vendor/msal-browser/5.25.0`.

1. In Entra **App registrations**, select the configured application or create
   one for WorkMonitor.
2. Under **Authentication**, add a **Single-page application** platform with
   redirect URI `https://localhost:7286/auth-callback.html`. A **Web** platform
   URI ending in `/signin-oidc` is not the SPA callback. Add the corresponding
   HTTPS SPA callback for each deployment origin. Do not enable implicit
   access/ID-token grants or public-client device/password flows for this flow.
3. Under **Expose an API**, set the Application ID URI to `api://<client-id>`.
   Add an enabled delegated scope named `access_as_user` (with a consent name
   and description such as "Access WorkMonitor as the signed-in user").
   Choose who can consent according to your tenant policy. Under
   **Authorized client applications**, authorize this same application's
   client ID for that scope; alternatively use the tenant's approved consent
   process. The SPA and API share this registration in this milestone.
4. In the application manifest, set `api.requestedAccessTokenVersion` to `2`.
   This controls tokens issued for the WorkMonitor API, not Azure DevOps.
5. Add the **Azure DevOps** delegated permission `user_impersonation`.
   Grant consent according to your tenant's policy; administrator consent
   may be required.
6. Configure the matching `AzureAd:TenantId` and `AzureAd:ClientId`. Do not
   create a secret/certificate or weaken tenant credential policies. SPA setup,
   exposing an API and consent can still require an application owner/admin.

Use `dotnet user-secrets set --project FT-MONITOR "AzureAd:ClientId" "YOUR-APP-ID"`
and the equivalent `AzureAd:TenantId` setting if you create a new registration.
These two IDs are public metadata. Model keys and other service credentials
belong in user-secrets locally or an approved production vault.

Run with `dotnet run --project FT-MONITOR --launch-profile https`. If necessary,
trust the local development certificate with `dotnet dev-certs https --trust`.
The sign-in helper reports missing configuration before starting sign-in.
`GET /api/auth/status` exposes the public client/authority/scope metadata.
Its `configured` field confirms local settings only, not that Azure has the
SPA callback, scope or consent configured. Browser registration/consent errors
must be resolved in Entra; the application does not fall back to server,
Visual Studio, Azure CLI or managed-identity DevOps access.

If Entra reports `AADSTS500011` for `api://<client-id>`, verify **Expose an API**
has that exact Application ID URI and the enabled `access_as_user` scope.
Verify the selected app's client ID and directory match local configuration.
If those are correct, an administrator may need to install/consent the API's
enterprise application (service principal) in that tenant. Changing the SPA
callback alone does not register the API resource.

The browser acquires two separate access tokens for the same selected account:
the WorkMonitor API token (`api://<client-id>/access_as_user`) is sent in
`Authorization: Bearer`, and Azure DevOps's delegated token is sent in
`X-WorkMonitor-DevOps-Token` only for requests that execute DevOps operations.
Our API validates its own token's signature, issuer, audience and expiry and
requires the delegated scope. Neither an ID token nor an Azure DevOps resource
token can authenticate the WorkMonitor API. Tokens are not placed in URLs,
chat messages or logs. API authorization does not accept cookies, so chat
writes do not require the former cookie-based CSRF preflight.

Azure DevOps resource tokens can be opaque. The server verifies their
authenticated identity through Azure DevOps connection data and Graph and
matches its Entra `domain`/`originId` to the API caller's verified `tid`/`oid`.
The selected account must have organization and Graph access. Missing tokens,
unverifiable identities and account mismatches fail explicitly; only the
verified request token is passed to the tools.

Web data defaults to `%LOCALAPPDATA%\WorkMonitor\Web`: `conversations.db`
holds user-owned chats outside the source tree. Existing `DpKeys` from the
previous cookie implementation are not used by SPA authentication.
Configure `WorkMonitor:Web:DataDirectory` to a writable,
access-controlled persistent directory in Azure (for example `/home/data/workmonitor-web`).
This milestone targets one app instance. A multi-instance deployment requires
shared storage and distributed turn locking. Browser token acquisition is
independent of server restarts; MSAL handles token renewal and may require
interaction again when the browser session or tenant policy requires it.

### Localhost-only development authentication

The default remains `SpaPkce`. If per-user API/DevOps consent is unavailable,
an explicitly enabled `LocalDevelopment` mode can use the original basic
Microsoft sign-in for the page and the developer's existing CLI Azure identity
for read-only DevOps access. This is not a shared or production authentication
mode, does not grant the SPA new permissions and cannot override tenant policy.

1. Authenticate the developer with the CLI's approved flow:
   `workmonitor --auth-login`. This must select the account configured in
   `WorkMonitor:Authentication:LoginHint`, in the configured tenant.
2. Keep the original Entra **Web** redirect URI
   `https://localhost:7286/signin-oidc` and the original ID-token sign-in
   configuration. Local mode requests only `openid profile email`, not the
   WorkMonitor API scope or Azure DevOps browser scopes. If the tenant does
   not permit this basic/legacy sign-in, approval is still required; do not
   disable tenant controls.
3. Opt in outside source control:

   ```powershell
   dotnet user-secrets set --project FT-MONITOR "WorkMonitor:Web:AuthenticationMode" "LocalDevelopment"
   ```

4. Run under the **Development** environment, bound only to loopback URLs
   (`https://localhost:7286;http://localhost:5191`). Production environments,
   wildcard/remote listeners and custom Kestrel endpoints fail startup.
5. Open the chat in Edge and select **Sign in** using the same developer
   account. A development warning identifies the mode and authenticated
   account. No Entra client secret or app-auth certificate is required.

The server loads the existing tenant/account-pinned, protected CLI cache with
interactive recovery disabled. Missing/expired authentication fails with an
actionable instruction to run `workmonitor --auth-login`; background web
requests never launch a login or select another host credential. Azure DevOps
verifies the cached token's Entra tenant/object IDs, and both initial OIDC
sign-in and subsequent cookie validation must match those IDs. Other local
users cannot inherit the developer's work access.

Raw remote IP and Host must be loopback before any other middleware. Proxy
headers are ignored. Cookies are secure/HttpOnly, restricted to this mode,
and backed by keys outside Git (`LocalDpKeys`; DPAPI-protected on Windows).
All unsafe MVC API calls require antiforgery tokens, including parameters,
skills and conversations. Local sign-out clears only the web cookie, not
the CLI's saved developer identity. Tools remain read-only.

Return to the approved per-user experience by setting the authentication
mode to `SpaPkce` and restarting. Existing user-owned conversations retain
their tenant/object-ID ownership across modes.

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
- For web chat, a configured Entra web app with delegated Azure DevOps consent.
  CLI and MCP acquire Azure DevOps tokens through their persistent user login.
- For the dev HTTPS cert: `dotnet dev-certs https --trust` (once per machine).

## Project layout

```
FT-MONITOR/
  Controllers/  ASP.NET MVC API controllers.
  Services/     Delegated web tokens, user identity, and turn coordination.
  wwwroot/      Static dashboard pages (HTML + JS, no framework).
src/
  WorkMonitor.Core/   Shared domain models and API clients.
  WorkMonitor.Agent/  Agent loop, tools, and persistent conversations.
  WorkMonitor.Cli/    Terminal host.
  WorkMonitor.Mcp/    MCP stdio host.
launch-ft-monitor.ps1   Dev-machine launcher (git pull -> build -> run -> open).
publish-portable.ps1    Produces a single-file portable build.
FT-MONITOR.cmd          CMD wrapper around launch-ft-monitor.ps1.
```
