# WorkMonitor

WorkMonitor is a .NET triage assistant for Azure DevOps feedback and Bugs, with
web dashboards, a conversational CLI, and local MCP tools for GitHub-aware
investigation.

It helps engineering, support, and feature teams turn feedback into actionable
work: find the items that need attention, understand their history, inspect
related GitHub issues, and prepare the next triage action. The web application
retains the original **FT-MONITOR** project name.

Typical workflows:

- Review work assigned to you, recent mentions, newest comments, and Azure DevOps
  pull requests waiting for your review.
- Run a team's saved feedback query, filter Bugs by area or assignee, and inspect
  work-item details.
- Use the web dashboards to summarize reports, explore feedback themes, rank
  relevant tickets, and draft customer responses for human review.
- Read issues in configured GitHub repositories and follow explicit references
  between GitHub and Azure DevOps.
- Preview a comment, field update, or linked engineering Bug in the CLI/MCP;
  perform a live write only through the safeguards described below.

## Contents

- [Choose an interface](#choose-an-interface)
- [Prerequisites](#prerequisites)
- [Quick start: conversational CLI](#quick-start-conversational-cli)
- [Configuration](#configuration)
- [Everyday CLI usage](#everyday-cli-usage)
- [Saved queries and project areas](#saved-queries-and-project-areas)
- [Local MCP usage](#local-mcp-usage)
- [Write operations and safeguards](#write-operations-and-safeguards)
- [Web dashboards and chat](#web-dashboards-and-chat)
- [Local data and security boundaries](#local-data-and-security-boundaries)
- [Troubleshooting](#troubleshooting)
- [Packaging and releases](#packaging-and-releases)
- [Architecture and repository map](#architecture-and-repository-map)

## Choose an interface

| Interface | Best for | Model and write behavior |
|---|---|---|
| **Web** (`FT-MONITOR`) | Visual dashboards, feedback analysis, and resumable browser chat | Azure OpenAI runs on the server. Chat has 12 read-only tools; no work-item comment, update, or Bug-creation tools. |
| **CLI** (`WorkMonitor.Cli`) | Conversational investigation and controlled triage from a terminal | Uses your Azure OpenAI deployment. Has 15 tools; writes start in dry-run mode and require per-operation approval in live mode. |
| **MCP** (`WorkMonitor.Mcp`) | Reusing the tools from an MCP-compatible assistant | Local **stdio**, not HTTP. Has the same 15 tools; the MCP client supplies the model. Writes require both server enablement and call-level confirmation. |

**Available here:** web feedback-theme analysis, web summaries/drafts, the shared
read tools, guarded field editing, and linked Bug creation. The separate
`analyze_feedback_themes` CLI/MCP tool and `get_work_item` `view="summary"`
extension are **not implemented in this checkout**. HTTP/remote MCP, Entra
on-behalf-of (OBO), Copilot Studio, and Teams integration are planned, not
implemented or deployed by these commands.

The current Azure DevOps integration targets the existing DevDiv process.
Organization, project, and team are source constants in
[`Constants.cs`](src/WorkMonitor.Core/Helpers/Constants.cs), not .NET
configuration settings. Supporting another
organization requires reviewing those constants, process-specific fields, and
dashboard links; changing a tenant ID or area mapping alone is not sufficient.

## Prerequisites

- **.NET SDK 8.0.424** for running/building from source. [`global.json`](global.json)
  pins that exact version with SDK roll-forward disabled. Self-contained packages
  do not require a separately installed .NET runtime.
- **PowerShell on Windows** for the examples below; Git is needed to clone/update
  source. Run commands from the repository root unless stated otherwise.
- **An approved Entra account** with access to the target Azure DevOps organization,
  project, queries, and work items. Identity-based reads also need the relevant
  Azure DevOps connection-data/Graph access. Writes require the corresponding
  Azure DevOps permissions and process rules.
- **An Azure OpenAI resource and deployed chat model** for the CLI and web host.
  MCP tools themselves do not require Azure OpenAI configuration.
- **Web only:** a configured Entra application and HTTPS callback/consent setup;
  see [web authentication](#web-authentication-spa-with-pkce) before signing in.
- **GitHub reads:** public repositories work without a token, subject to API
  limits. Private repositories need an appropriately scoped, organization-approved
  token with access to those repositories.

## Quick start: conversational CLI

After cloning the repository, replace the placeholders and run this in PowerShell:

```powershell
# Pin the Azure account used for Azure DevOps and, when keyless, Azure OpenAI.
$env:WorkMonitor__Authentication__TenantId = "YOUR-TENANT-ID"
$env:WorkMonitor__Authentication__LoginHint = "you@example.com"

$env:AzureOpenAI__Endpoint = "https://YOUR-RESOURCE.cognitiveservices.azure.com/"
$env:AzureOpenAI__DeploymentName = "YOUR-DEPLOYMENT"

# Optional: omit this when the selected Azure identity has model access.
# $env:AzureOpenAI__ApiKey = "YOUR-AZURE-OPENAI-KEY"

dotnet run --project .\src\WorkMonitor.Cli -- --auth-login
dotnet run --project .\src\WorkMonitor.Cli -- --check
dotnet run --project .\src\WorkMonitor.Cli
```

In the terminal conversation, start with:

```text
List my 10 most recently changed active work items.
Show the Azure DevOps pull requests waiting for my review.
List the configured project areas and saved queries.
```

`--auth-login` opens the approved interactive browser login. CLI and MCP share a
protected persistent Azure token cache and a tenant/account-pinned authentication
record. `--check` reports local model settings, the saved identity, and the session
database path; it is **not** a live Azure DevOps/model connectivity or permission
test. The CLI starts with writes disabled by dry-run mode.

## Configuration

Environment variables use `__` in place of `:` for nested .NET configuration.
For CLI/MCP, environment variables override user-secrets and the host's
`appsettings.json`. The web host uses standard ASP.NET Core configuration:
user-secrets are loaded in Development, and environment variables override them.
Environment settings in one PowerShell process do not automatically configure a
separately launched MCP client, terminal, or deployed web app.

| Setting | Purpose |
|---|---|
| `AzureOpenAI:Endpoint` | Azure OpenAI **resource** endpoint, not a Foundry project-management endpoint. Required by CLI/web. |
| `AzureOpenAI:DeploymentName` | Your deployed chat model's deployment name, not necessarily its base model name. Required by CLI/web. |
| `AzureOpenAI:ApiKey` | Optional model credential. Without a key, CLI uses its saved user credential. Web SPA mode uses `DefaultAzureCredential` (including configured managed identity); LocalDevelopment mode uses its injected CLI credential. |
| `WorkMonitor:Authentication:TenantId` | Required CLI/MCP tenant pin. Also used by localhost development web authentication. |
| `WorkMonitor:Authentication:LoginHint` | Expected account; set it to prevent selecting a different user's Azure identity. Required for localhost development web authentication. |
| `AzureAd:TenantId`, `AzureAd:ClientId` | Web SPA/API application registration. These are public metadata, not client secrets. |
| `GitHub:Token` | Optional GitHub read token; required for private repositories. |
| `GitHub:Repositories` | Repository catalog with `name`, `owner`, and `repo` entries. Tools use `name` as their `area` argument. |
| `AzureDevOps:SavedQueries` | Fallback query catalog with `name` and `queryId` entries; a saved-query data file takes precedence. |
| `WorkMonitor:DataDirectory` | CLI/MCP session, authentication-record, and parameter-data root. |
| `WorkMonitor:Web:DataDirectory` | Web conversation/local-auth-key directory; does **not** move web area/query files. |
| `WorkMonitor:Mcp:AllowWrites` | Defaults to `false`. Enabling it does not replace the required `confirm=true` on each MCP write. |
| `WorkMonitor:Web:AuthenticationMode` | Web default is `SpaPkce`; `LocalDevelopment` is an explicit localhost-only alternative. |
| `WorkMonitor:MaxContextCharacters`, `WorkMonitor:RetainRecentMessages` | CLI conversation-compaction settings; defaults are 60000 and 12. |

### Store credentials outside source control

All three hosts share the existing .NET user-secrets ID, so settings saved through
the CLI project are also available to MCP and to the web host in Development:

```powershell
dotnet user-secrets set --project .\src\WorkMonitor.Cli "WorkMonitor:Authentication:TenantId" "YOUR-TENANT-ID"
dotnet user-secrets set --project .\src\WorkMonitor.Cli "WorkMonitor:Authentication:LoginHint" "you@example.com"
dotnet user-secrets set --project .\src\WorkMonitor.Cli "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.cognitiveservices.azure.com/"
dotnet user-secrets set --project .\src\WorkMonitor.Cli "AzureOpenAI:DeploymentName" "YOUR-DEPLOYMENT"

# Optional alternatives to keyless model access / anonymous public GitHub reads:
dotnet user-secrets set --project .\src\WorkMonitor.Cli "AzureOpenAI:ApiKey" "YOUR-AZURE-OPENAI-KEY"
dotnet user-secrets set --project .\src\WorkMonitor.Cli "GitHub:Token" "YOUR-GITHUB-TOKEN"
```

User-secrets live outside the repository at
`%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` on Windows; they
are development configuration, not an encrypted production vault. Secret fields
in tracked `appsettings.json` files are empty placeholders. Never commit real
keys/tokens, copy them into chat prompts, or include them in shared packages.

To configure a GitHub area through environment variables:

```powershell
$env:GitHub__Repositories__0__name = "product"
$env:GitHub__Repositories__0__owner = "YOUR-GITHUB-OWNER"
$env:GitHub__Repositories__0__repo = "YOUR-REPOSITORY"
```

This overrides catalog entry `0`; it does not remove any other configured entries.
The shipped tenant, repository, area, and query defaults are deployment-specific,
not universal settings. Review the effective catalog before investigating.

## Everyday CLI usage

Run a single prompt, or omit `--prompt` for the interactive conversation:

```powershell
dotnet run --project .\src\WorkMonitor.Cli -- --prompt "Show my recent activity from the last 7 days, with the newest comment per item."
dotnet run --project .\src\WorkMonitor.Cli -- --prompt "Find feedback about project reload in the Project area."
dotnet run --project .\src\WorkMonitor.Cli -- --prompt "List open GitHub issues in the product area."
```

`Project` is a configured Azure DevOps area key; `product` is the GitHub area from
the configuration example above. Use your actual configured names.

| Interactive command | What it does |
|---|---|
| `/new`, `/sessions`, `/resume <session-id>` | Start, list, or resume persisted conversations. Startup resumes the latest conversation by default. |
| `/model`, `/tools`, `/help` | Show the active deployment, available tools, or command help. |
| `/working-set`, `/working-set add <id> [type]`, `/working-set clear` | Inspect, pin, or clear work-item context. The working set is cleared when starting/resuming a session. |
| `/dry-run` | Preview writes without submitting them; the default for each CLI launch. |
| `/live` | Enable live writes, still subject to a separate approval for every operation. |
| `/audit` | Show persisted write-audit entries for the current conversation. |
| `/exit` | Leave the CLI. Ctrl+C cancels the active turn. |

The CLI stores conversations, compaction summaries, and write audits locally.
Read results can be bounded or truncated: inspect counts and partial-result flags,
then retrieve a specific item rather than assuming a returned page is exhaustive.

## Saved queries and project areas

Start with `list_project_areas` and `list_saved_queries` in chat or an MCP client.
**Query discovery lists WorkMonitor's configured name-to-ID mappings**, not every
query or folder in Azure DevOps. Get an existing saved query's GUID from its Azure
DevOps query link and register it; WorkMonitor does not create that remote query
or grant query permissions.

Run a registered query by friendly name, or pass an accessible query GUID directly:

```text
List the saved queries available in WorkMonitor.
Run the saved query "Team feedback" and return at most 20 items.
```

For MCP, call `execute_saved_query` with:

```json
{
  "query": "Team feedback",
  "maxResults": 20
}
```

The agent/MCP tool accepts `maxResults` from 1 to 50 and reports `totalCount` and
`returnedCount`. Query access and `@Me` are evaluated using the authenticated user,
not the server's model identity.

### Configure the catalog

For the web host, sign in and open **Parameters** at
`https://localhost:7286/parameters.html` to maintain saved-query names/IDs and area
aliases. These are application-configuration changes, not Azure DevOps work-item
writes. The Feedback Tickets page also has a saved-query selector.

CLI/MCP and web do **not** automatically share these parameter files:

| Host | Area mappings | Saved queries |
|---|---|---|
| CLI/MCP | `%LOCALAPPDATA%\WorkMonitor\data\parameters.json` | `%LOCALAPPDATA%\WorkMonitor\data\saved-queries.json` |
| Web | `FT-MONITOR\data\parameters.json` | `FT-MONITOR\data\saved-queries.json` |

CLI/MCP paths are relative to `WorkMonitor:DataDirectory` if overridden. Web
parameter files stay under its content root, even when
`WorkMonitor:Web:DataDirectory` changes.

To maintain CLI/MCP mappings manually, stop those hosts, create the `data`
directory/file if needed, and restart after editing. A saved-query file is a JSON
object, not the configuration array:

```json
{
  "Team feedback": "YOUR-SAVED-QUERY-GUID"
}
```

An area file uses friendly keys and full Azure DevOps area paths:

```json
{
  "Project": "YOUR-PROJECT\\YOUR-ENGINEERING-AREA"
}
```

Use real GUIDs/paths accessible to your account. If no saved-query file is
present, `ParametersService` reads `AzureDevOps:SavedQueries` entries from .NET
configuration (`name`/`queryId`). If a valid saved-query file exists, it wins,
including an empty `{}`; changing environment/user-secrets query settings will not
override it. A missing or empty area file instead falls back to the built-in
area mappings. Confirm the result with the discovery tools after restarting.

## Local MCP usage

Authenticate once through the CLI using the same Windows account, tenant,
login hint, and data directory that the MCP process will use. Then inspect tools
or run the stdio host from source:

```powershell
dotnet run --project .\src\WorkMonitor.Mcp -- --list-tools
dotnet run --project .\src\WorkMonitor.Mcp
```

The second command waits for an MCP client over stdin/stdout; it does not start
a web server. Logs go to stderr. The client, not WorkMonitor.Mcp, controls the
assistant/model and conversation UI.

For routine client use, [publish the executables](#packaging-and-releases) and
point your MCP client's local-server configuration at the MCP executable.
For clients that use a `mcpServers` object, an illustrative entry is:

```json
{
  "mcpServers": {
    "workmonitor": {
      "command": "C:\\PATH\\TO\\workmonitor\\artifacts\\workmonitor\\win-x64\\mcp\\workmonitor-mcp.exe",
      "args": [],
      "env": {
        "WorkMonitor__Authentication__TenantId": "YOUR-TENANT-ID",
        "WorkMonitor__Authentication__LoginHint": "you@example.com",
        "WorkMonitor__Mcp__AllowWrites": "false"
      }
    }
  }
}
```

Replace the executable path and metadata; adapt the wrapper to your client's
configuration format. This is **client configuration**, not WorkMonitor's
`appsettings.json`, a remote endpoint, or a Teams installation. Configure GitHub
catalog/token settings separately as needed. Prefer the CLI login before starting
the client so authentication does not interrupt tool use.

### Read tools shared by all three agents

| Workflow | Tools |
|---|---|
| Discover configuration | `list_project_areas`, `list_saved_queries` |
| Find and inspect work | `search_work_items`, `get_work_item` |
| Personal triage | `list_my_work_items`, `list_items_mentioning_me`, `list_my_recent_activity` |
| Run a saved query | `execute_saved_query` |
| Pending Azure DevOps reviews | `list_pull_requests_for_my_review` |
| GitHub issue reads | `list_github_issues`, `get_github_issue` |
| Cross-source investigation | `correlate_work_item_with_github` |

For example, `list_github_issues` accepts
`{"area":"product","state":"open","maxResults":10}`. For correlation, pass
`{"workItemId":12345,"area":"product","maxResults":5}`. Numeric IDs in this README
are illustrative; replace them with items you are authorized to access.

Correlation follows **explicit references**, not inferred thematic similarity,
and is limited to configured GitHub repositories. Recent activity and correlation
preserve their JSON structure under an aggregate output-size budget. Partial
flags identify output truncation, candidate windows, and bounded DevOps comment
scans. Use `get_work_item` or `get_github_issue` for additional details; these
results are not an exhaustive audit of every comment or related item.

## Write operations and safeguards

Only CLI/MCP expose `add_work_item_comment`, `update_work_item`, and
`create_bug_from_feedback`. Web chat deliberately omits all three; generated
web summaries/customer replies are drafts, not posted responses.

| Host | Default | Required for a live write |
|---|---|---|
| CLI | Dry run | `/live` **and** explicit approval for each proposed operation. `/dry-run` restores preview-only behavior. |
| MCP | Dry run | `WorkMonitor__Mcp__AllowWrites=true` on the server **and** `confirm=true` in that call. Either gate missing means dry run. |
| Web chat | Read-only tools | No live work-item write path. Parameters/skills/conversation changes are separate application operations. |

Dry-run tool execution previews the payload without authenticating or contacting
Azure DevOps. It does **not** verify server revisions, source existence, process
rules, identities, or permissions. Conversational CLI prompts can still invoke
the model or read tools around that preview. Once a live write is dispatched,
cancellation does not interrupt observation of its outcome.

CLI approval decisions and tool write audits are persisted with the conversation.
MCP write tools use per-call in-memory tool audit entries, not the CLI's persisted
conversation audit; do not assume `/audit` covers external MCP calls.

### Add a work-item comment

`add_work_item_comment` takes `id` and `text` (an HTML comment body). MCP also
accepts `confirm`, defaulting to `false`:

```json
{
  "id": 12345,
  "text": "<p>Triage note for review.</p>",
  "confirm": false
}
```

Posting a comment is a separate operation; neither field editing nor linked Bug
creation implicitly posts one.

### Update selected fields

Call `get_work_item` first and copy its `revision` into `expectedRevision`.
For `update_work_item`, supply a nonempty subset of the six allowlisted fields.
The example includes MCP's optional `confirm` flag; CLI tool arguments omit it:

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

Omitted or `null` fields remain unchanged. An empty description clears it,
`assignedTo: ""` unassigns the item, and `tags: []` clears all tags.
A supplied tag list replaces the entire list
(individual tags cannot contain semicolons). Titles must be nonblank and at most
255 characters. Priorities are nonnegative integers whose allowed values depend
on the work item type/process, not a universal 1-to-4 scale: current DevDiv Bugs
allow 0, 1, 2, 3 and FeedbackTickets allow 0, 1, 2. Live updates defer these
type-specific constraints to Azure DevOps. State transitions and identities
also remain subject to process rules and the signed-in user's permissions.
Unknown fields and empty updates are rejected. This tool cannot create work
items or change relationships.

Revision, identity, permission, and process validation happen on the live PATCH.
Live updates atomically test `/rev` before changing only the requested fields.
Stale revisions fail without an automatic retry: re-read the item, review the
new state, and explicitly approve a fresh update. The shared CLI approval/audit
and MCP dual write gates apply.

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
    "areaPath": "YOUR-PROJECT\\YOUR-ENGINEERING-AREA",
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

## Web dashboards and chat

Configure Azure OpenAI and complete the Entra setup below, then run:

```powershell
dotnet build .\FT-MONITOR\FT-MONITOR.csproj
dotnet run --project .\FT-MONITOR --launch-profile https
```

Open `https://localhost:7286/` and sign in. The launch profile may initially open
Swagger; the dashboard is at `/`.

| Page | Usage |
|---|---|
| `/my-dashboard.html` | Review assigned/mentioned work and recently changed items. |
| `/top-feedback.html`, `/top-bugs.html` | Select an area/assignee, search reports, inspect details, summarize, or draft a response. Feedback also supports saved queries. |
| `/feedback-insights.html`, `/feedback-dashboard.html`, `/most-relevant.html` | Explore AI-assisted feedback insights, themes, and relevant-ticket ranking. |
| `/github-issues.html` | Select a configured repository, filter issues, inspect threads, and request summaries. |
| `/parameters.html` | Maintain web area aliases and saved-query mappings. |
| `/agent.html` | Use the read-only conversational agent with saved sessions and streaming progress. |

AI classifications, rankings, summaries, and drafts are assistance, not verified
facts or automatically sent replies. Review source evidence and remove sensitive
internal context before sharing generated text.

The optional `FT-MONITOR.cmd` launcher fetches/pulls the current branch, builds,
runs, and opens the browser. To keep the current checkout without a Git update,
use `.\launch-ft-monitor.ps1 -NoPull`; `-NoBuild` skips its build step.

### Web authentication: SPA with PKCE

Open `https://localhost:7286/agent.html`. Conversations are persisted and
isolated by the signed-in user's Entra tenant and object ID. Responses and
tool progress stream over authenticated NDJSON. Cancelling a turn preserves
the conversation. The browser uses MSAL Browser authorization code flow with
PKCE. No Entra client secret, application certificate, or server-side cookie
sign-in is required. The Azure OpenAI API key still authenticates the model
only and must remain on the server.

The supported MSAL Browser 5.25.0 bundle and callback bridge are served locally;
their MIT license, pinned SHA-256 provenance and integrity attributes are
included under `FT-MONITOR\wwwroot\vendor\msal-browser\5.25.0`.

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

Configure the approved registration from the repository root:

```powershell
dotnet user-secrets set --project .\FT-MONITOR "AzureAd:TenantId" "YOUR-TENANT-ID"
dotnet user-secrets set --project .\FT-MONITOR "AzureAd:ClientId" "YOUR-APP-ID"
```

These two IDs are public metadata. Model keys and other service credentials
belong in user-secrets locally or an approved production vault.

Run with `dotnet run --project .\FT-MONITOR --launch-profile https`. If necessary,
trust the local development certificate with `dotnet dev-certs https --trust`.
The sign-in helper reports missing configuration before starting sign-in.
`GET /api/auth/status` exposes the public client/authority/scope metadata.
Its `configured` field confirms local settings only, not that Azure has the
SPA callback, scope or consent configured. Browser registration/consent errors
must be resolved in Entra; the application does not fall back to server,
Visual Studio, Azure CLI or managed-identity DevOps access.

For `AADSTS500011` resource-registration errors, see
[troubleshooting](#troubleshooting).

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

Browser token acquisition is independent of server restarts; MSAL handles token
renewal and may require interaction again when the browser session or tenant
policy requires it. See [local data](#local-data-and-security-boundaries) for
conversation storage and deployment limits.

### Localhost-only development authentication

The default remains `SpaPkce`. If per-user API/DevOps consent is unavailable,
an explicitly enabled `LocalDevelopment` mode can use the original basic
Microsoft sign-in for the page and the developer's existing CLI Azure identity
for read-only DevOps access. This is not a shared or production authentication
mode, does not grant the SPA new permissions and cannot override tenant policy.

1. Authenticate the developer with the CLI's approved flow:
   `dotnet run --project .\src\WorkMonitor.Cli -- --auth-login` (or
   `workmonitor --auth-login` for a published CLI). Set
   `WorkMonitor:Authentication:TenantId` and `LoginHint` as above; the web
   `AzureAd:TenantId` must match. Use the same CLI data directory/account record.
2. Keep the original Entra **Web** redirect URI
   `https://localhost:7286/signin-oidc` and the original ID-token sign-in
   configuration. Local mode requests only `openid profile email`, not the
   WorkMonitor API scope or Azure DevOps browser scopes. If the tenant does
   not permit this basic/legacy sign-in, approval is still required; do not
   disable tenant controls.
3. Opt in outside source control:

   ```powershell
   dotnet user-secrets set --project .\FT-MONITOR "WorkMonitor:Web:AuthenticationMode" "LocalDevelopment"
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

Return to the approved per-user experience with:

```powershell
dotnet user-secrets set --project .\FT-MONITOR "WorkMonitor:Web:AuthenticationMode" "SpaPkce"
```

Restart the host. Existing user-owned conversations retain
their tenant/object-ID ownership across modes.

## Local data and security boundaries

| Data | Default Windows location |
|---|---|
| CLI conversations, summaries, persisted write audits | `%LOCALAPPDATA%\WorkMonitor\workmonitor.db` |
| CLI/MCP non-secret account selection | `%LOCALAPPDATA%\WorkMonitor\authentication-record.json` |
| CLI/MCP area/query catalogs | `%LOCALAPPDATA%\WorkMonitor\data\` |
| Web user-owned conversations | `%LOCALAPPDATA%\WorkMonitor\Web\conversations.db` |
| LocalDevelopment cookie-protection keys | `%LOCALAPPDATA%\WorkMonitor\Web\LocalDpKeys\` |

CLI/MCP use the same OS-protected Azure token cache. `--auth-reset` clears the
saved account selection, **not** all cached tokens, chat history, or audit data.
Use it before signing in again when changing tenant/account. Web conversations
are owned by the verified Entra tenant/object ID; the web store is separate from
the CLI session store. Existing `DpKeys` from the previous cookie implementation
are not used by SPA authentication. Set `WorkMonitor:Web:DataDirectory` to a
writable, access-controlled persistent directory on a deployment host.

Protect these directories and backups: conversations, tool results, drafts, and
audits can contain work-item/customer data even though the authentication record
itself is non-secret. Send only approved data to the configured model endpoint.
Never package token caches, databases, user-secrets, or authentication records.

**Deployment limitation:** this is triage/development tooling, not a
production-hardened service. The existing Bugs/Feedback Tickets pages still need
stored-XSS remediation in work-item HTML rendering before shared deployment.
Authentication and write guards are not security approval. Web conversation
coordination currently targets one instance; multiple instances require shared
storage and distributed turn locking.

Use approved environment/vault mechanisms for deployed secrets. Azure Key Vault
is **not wired in by default**; direct vault loading requires adding
`AddAzureKeyVault(...)` and its deployment configuration. These setup commands
do not grant tenant permissions, create a Teams app, or deploy a remote MCP host.

## Troubleshooting

| Symptom | Check or action |
|---|---|
| SDK resolution fails | Install the exact SDK in `global.json`; another .NET 8 SDK is not enough while roll-forward is disabled. |
| Missing endpoint/deployment | Set `AzureOpenAI:Endpoint` and `DeploymentName` in the process running CLI/web. `--check` validates local presence, not live access. |
| Wrong saved Azure identity | Run `--check`, then `--auth-reset` and `--auth-login` with the correct tenant and login hint. Reset does not revoke tokens or delete conversations. |
| `401 PermissionDenied` from Azure OpenAI | For keyless access, the selected identity needs **Cognitive Services OpenAI User** on the resource owning the deployment. Check identity, endpoint, and deployment. The web model credential can differ from the browser's DevOps identity. |
| Browser sign-in/consent fails | Verify the SPA callback, API scope, token version, client/tenant IDs, and approved consent in Entra. `/api/auth/status` checks local metadata only. |
| `AADSTS500011` for `api://<client-id>` | Verify **Expose an API** has that exact Application ID URI and enabled `access_as_user` scope. If IDs/tenant match, an administrator may need to install/consent the API's enterprise application. Changing the callback alone does not register the API resource. |
| DevOps identity cannot be verified | Sign in with the same account for API and DevOps tokens; ensure organization/Graph access. The web host does not fall back to another identity. |
| Unknown query or area | Call the discovery tools; check the correct host's data file and file-over-configuration precedence. A query GUID still needs Azure DevOps permission. |
| MCP process waits in the terminal | Expected for stdio; connect it through an MCP client. There is no HTTP URL or browser page. |
| MCP write remains a preview | Both server `AllowWrites=true` and per-call `confirm=true` are required. Web chat never exposes these writes. |
| Field update conflicts | Re-read `get_work_item`, review the new state, and explicitly approve a new update using the fresh revision. Do not retry blindly. |
| Bug-creation outcome is uncertain | Search for and verify the possible Bug and its Related link before any newly approved attempt. A timeout is not proof that no Bug was created. |

For a local HTTPS certificate trust error, use the normal developer-certificate
setup on your machine, then retry:

```powershell
dotnet dev-certs https --trust
```

Do not bypass browser certificate warnings or weaken tenant security policies to
make sign-in succeed.

## Packaging and releases

### CLI and MCP executables

Publish self-contained Windows x64 hosts:

```powershell
.\publish-workmonitor.ps1

.\artifacts\workmonitor\win-x64\cli\workmonitor.exe --help
.\artifacts\workmonitor\win-x64\mcp\workmonitor-mcp.exe --list-tools
```

Outputs are separated under `artifacts\workmonitor\<runtime>\cli` and `mcp`.
The script also accepts `-Runtime`, `-OutputDirectory`, and a semantic `-Version`.
Recipients still need their own authorized configuration and Azure login.

### Portable web host

```powershell
.\publish-portable.ps1
# Inspect the output for credentials/data before creating a shareable archive.
Compress-Archive -Path .\publish\* -DestinationPath .\FT-MONITOR-portable.zip -Force
```

The Windows x64 web executable bundles .NET, but not authorization, a configured
model, or a trusted HTTPS certificate. The recipient unpacks the archive and runs
`Run-FT-MONITOR.cmd` only after configuring those prerequisites. The generated
launcher uses **Production**, so development user-secrets and `LocalDevelopment`
authentication do not apply. Configure approved environment secrets and the SPA
registration. Never embed real credentials in `publish\appsettings.json` before
sharing; the publisher includes that file and web content/data in its output.

### Maintainer releases

Tags matching `v*` trigger the
[`release.yml`](.github/workflows/release.yml) workflow. Creating/pushing a release
tag is a separate publishing action, not part of local setup. For an authorized
release using a new semantic version, for example:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The workflow builds/tests the solution, publishes versioned `win-x64` and
`linux-x64` hosts, creates separate CLI/MCP archives, generates `SHA256SUMS.txt`,
and attaches them to a GitHub Release. It does not publish the web host.
Archives include only each self-contained executable and placeholder
`appsettings.json`, not local identities or conversation data. Verify downloaded
archives against the checksums. Protect `v*` tags against updates/deletion through
an approved repository ruleset to keep release tags immutable.

## Architecture and repository map

The CLI and browser chat use the shared agent runtime to stream model responses
and dispatch tools. MCP calls the same tool implementations directly without
running that model loop. The shared core talks to Azure DevOps/GitHub; web
controllers also provide dashboard-specific analysis endpoints. Azure OpenAI
authenticates separately from the user-scoped Azure DevOps calls.

| Path | Responsibility |
|---|---|
| [`FT-MONITOR`](FT-MONITOR) | ASP.NET Core host, MVC API controllers, web identity/turn coordination, and static HTML/JavaScript dashboards under `wwwroot` (no frontend build framework). |
| [`src\WorkMonitor.Core`](src/WorkMonitor.Core) | Azure DevOps/GitHub clients, domain models, parameter catalogs, and persistent user credentials. |
| [`src\WorkMonitor.Agent`](src/WorkMonitor.Agent) | Azure OpenAI adapter, streaming conversation loop, tools, write approvals, compaction, and SQLite session storage. |
| [`src\WorkMonitor.Cli`](src/WorkMonitor.Cli) | Interactive/single-prompt terminal host and persistent CLI write audits. |
| [`src\WorkMonitor.Mcp`](src/WorkMonitor.Mcp) | Official MCP SDK stdio host, tool schemas, and per-call write gates. |
| [`tests\WorkMonitor.Tests`](tests/WorkMonitor.Tests) | Core, agent, CLI/MCP, authentication, conversation, and write-safeguard tests. |
| [`FT-MONITOR.sln`](FT-MONITOR.sln), [`global.json`](global.json) | Solution entry point and exact SDK selection. |
| [`launch-ft-monitor.ps1`](launch-ft-monitor.ps1), [`FT-MONITOR.cmd`](FT-MONITOR.cmd) | Optional Windows update/build/run launcher. |
| [`publish-workmonitor.ps1`](publish-workmonitor.ps1), [`publish-portable.ps1`](publish-portable.ps1) | CLI/MCP and web self-contained packaging, respectively. |
| [`.github\workflows`](.github/workflows), [`infra`](infra) | CI/release/deployment workflow definitions and infrastructure templates; not evidence of an active deployment. |
