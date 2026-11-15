# FT-MONITOR

Internal feedback / bug / GitHub-issues triage dashboard for the DevDiv tooling
team. ASP.NET Core (.NET 10) app with Microsoft Entra sign-in, Azure DevOps
queries, Azure OpenAI summarization, and GitHub Issues integration.

## WorkMonitor conversational CLI

The repository includes a reusable agent stack and dedicated CLI/MCP hosts:

- `src/WorkMonitor.Core` contains the Azure DevOps and GitHub integrations.
- `src/WorkMonitor.Agent` contains the streaming conversation runtime and tools.
- `src/WorkMonitor.Cli` provides a resumable terminal conversation.
- `src/WorkMonitor.Mcp` exposes the same tools over the official MCP stdio SDK.
- `src/WorkMonitor.Mcp.Http` is a separate, authenticated, read-only remote MCP
  host (see [HTTP MCP Stages 2-3](#http-mcp-stages-2-3)).

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

The CLI and stdio MCP host share one encrypted persistent token cache for Azure OpenAI
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
- AI feedback theme analysis by configured area or saved query, with ticket links and coverage metadata
- Azure DevOps pull requests awaiting the current user's review
- Recent assigned/mentioned work with newest comments
- Work-item details and text search
- GitHub issue listing and details
- Explicit-reference correlation between DevOps work items and GitHub issues

Recent activity, correlation, ticket lookup, and theme results preserve their
JSON structure under output-size budgets. Partial-result flags expose truncated output
or candidate windows; use `get_work_item` or `get_github_issue` for additional
details. Correlation only follows explicit references in configured GitHub
repositories, and reports when its bounded DevOps comment scan is partial.

The CLI and stdio MCP hosts use the persisted interactive user credential. The HTTP
host instead requires a validated API bearer token and confidential-client OBO. The web
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

The CLI and MCP host expose 16 tools, including three write tools:
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

### Analyze feedback themes outside the web

`analyze_feedback_themes` runs the dashboard's shared, read-only theme analysis
directly against Azure DevOps and your configured Azure OpenAI deployment.
No FT-MONITOR server, dashboard sign-in, or local web endpoint is needed. The CLI
and MCP use their existing saved user credential, including for Azure OpenAI
when no API key is configured. MCP startup, `--list-tools`, and all previous
tools still work without Azure OpenAI settings; only theme analysis of nonempty
feedback requires them.

Choose **one source**: `projectArea` from `list_project_areas`, or `query` from
`list_saved_queries` (a configured alias or an accessible saved-query GUID).
Omitting both selects `Project`; supplying both is rejected.
`maxResults` defaults to 20 and must be 1-50.

For FTS, configure your environment's saved query instead of relying on a
built-in GUID. For example, before starting CLI or MCP:

```powershell
$env:AzureDevOps__SavedQueries__3__name = "FTMonitor - FTS"
$env:AzureDevOps__SavedQueries__3__queryId = "YOUR-FTS-SAVED-QUERY-GUID"
```

If `%LOCALAPPDATA%\WorkMonitor\data\saved-queries.json` already exists, that
saved-query catalog takes precedence over environment configuration. Add the
alias and actual GUID there instead (or use your configured `WorkMonitor:DataDirectory`).
Use `list_saved_queries` to verify the available names. No FTS GUID is assumed
or installed by this feature.

CLI prompt:

```text
Analyze feedback themes using the saved query "FTMonitor - FTS", with maxResults 20.
Show the themes, source ticket links, and any coverage limits.
```

MCP `tools/call` request, after normal MCP initialization:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "analyze_feedback_themes",
    "arguments": { "query": "FTMonitor - FTS", "maxResults": 20 }
  }
}
```

For area parity, use `{"projectArea":"Project","maxResults":50}` instead.
The result contains structured `themes` (`theme`, `description`,
`workItemIds`, verified `count`) and `items` (id, title, state, priority,
assignedTo, createdDate, internal browser `url`). `source` describes the
selection, candidate window, retrieved/missing items, and non-feedback items
excluded from a saved query. Area selection uses the same active FeedbackTicket
filters and created-date/priority ordering as the web; saved queries preserve
their own ordering and filters. Non-feedback items within the selected window
are excluded, not replaced by later results.

Queries request at most `maxResults + 1` rows to detect a partial window.
`source.observedCount` is a lower bound; `source.totalCount` is **null** when
the bounded query cannot establish an exact total. This is not a full-query
count. `coverage` reports unique analyzed/classified counts, unclassified ids,
payload-omitted ids, clipped-text ids, and output-omitted ids. Descriptions are
clipped to 2,000 characters, titles to 256, and the serialized AI input is capped
at 100,000 characters. CLI/MCP results also fit the existing 14,000-character
read-result budget: omitted output ids are reported and returned themes retain
only ids with a returned lookup entry, with counts recomputed. Coverage counts
continue to describe the analyzed input, not just the returned subset. Themes
also fit the remaining conversational turn's aggregate budget; they are not
replaced with sliced JSON previews.

Do not treat `coverage.isPartial` results as exhaustive. Empty feedback skips
the model entirely. Invalid JSON, unsupported ids, duplicate ids within a theme,
and mismatched counts fail explicitly rather than inventing or silently fixing
classification. Cancellation and source/model authorization errors propagate;
no ticket is written. A theme is a grouping, **not a confirmed duplicate**.
Ticket contents and model labels are untrusted data.
The web endpoint keeps its existing `{themes: JSON string, items: [...]}` shape
and drilldown, adding source/coverage fields and a partial-analysis notice.
Other dashboard analyses and web chat tool availability are unchanged.

### Look up a ticket by id

`get_work_item` now accepts an explicit `view` selector: `"summary"` or
`"detail"`. Existing calls that supply only `id` keep the **detail** view and
its existing fields/identity shape. For compact read clients, including the
later HTTP/Teams stage, use summary explicitly:

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tools/call",
  "params": {
    "name": "get_work_item",
    "arguments": { "id": 12345, "view": "summary" }
  }
}
```

The summary result is a stable object containing `id`, `revision`, `title`,
`state`, nullable `priority`, `assignedTo` (display name, identity name when
no display name is available, or `"Unassigned"`), canonical internal `url`,
and `coverage`. Summary uses one projected DevOps work-item read: it neither
requests descriptions nor fetches internal comments or customer discussions.
Missing priority remains `null`, not an invented priority zero.

CLI prompt:

```text
Look up ticket 12345 with get_work_item view=summary. Show its title, state,
priority, assignee, and ticket link. Do not fetch discussions.
```

Request `{"id":12345,"view":"detail"}` when descriptions, comments, or editable
fields are needed. Detail preserves `revision`, `tags`, the `assignedTo`
identity object, `description`, `createdBy`, `postId`, `postTitle`, `postContent`,
`comments` (`text`, `modifiedDate`) and `customerComments` (`text`, `author`,
`description`), adding the same canonical `url` and coverage metadata.
Initial lookup accepts a positive integer id only, not a URL or arbitrary
URL-fetch instruction.

CLI/MCP detail reads request a single page of at most 20 newest internal
comments and retain at most 20 returned customer comments; they do not claim
to have scanned a full discussion. `coverage.comments` and
`coverage.customerComments` report `totalCount` (null when unknown),
`sourceReturnedCount`, `returnedCount`, and `isPartial`. The customer endpoint
does not guarantee complete history, so a linked customer discussion remains
partial even when all returned entries fit.

All lookup results fit the existing 14,000-character serialized read budget
and, in a conversation, the remaining 16,000-character aggregate turn budget.
Optional detail text starts at 2,000 characters, comment text at 1,000;
whole comments and optional text can be reduced further to fit. Required
lookup fields, the canonical link, and revision remain structured.
`coverage.view`, `isPartial`, `outputTruncated`, and `truncatedFields` expose
clipping and omission; discussion `returnedCount` describes the entries
actually present. A budget too small for even lookup metadata returns an
explicit error, not a partial JSON preview. Once a conversation's read budget
is exhausted, further reads are skipped with an explicit truncation reason;
write approvals and outcome handling are unchanged. Do not build field
replacement writes from fields listed as truncated.

The ticket id/revision and required fields are verified **before** any
discussion read. Missing, inaccessible, mismatched, or malformed tickets fail
explicitly and never return success-shaped null details; denied-response
content is not included in the error. Discussion failures also fail explicitly.
When a comment supplies `workItemId`, it must be an integer matching the
requested ticket, or the entire read fails before projecting comments or
fetching linked customer discussion. Omitted ownership remains supported:
the [generated Azure DevOps client contract](https://github.com/microsoft/azure-devops-node-api/blob/master/api/interfaces/WorkItemTrackingInterfaces.ts)
marks `workItemId` optional; comment identity may be `id` or `commentId`
across the documented client/example shapes and is not used as ticket ownership.
Lookup requires only the existing DevOps authentication, not Azure OpenAI.
The web `/api/work-item-details/{id}` response remains detailed with the same
fields and full returned text/discussion page, adding URL/coverage metadata.
Its delegated user-token behavior and the 12 read-only web-chat tools remain
unchanged. This is the Stage 1 CLI/stdio contract; HTTP/OBO is implemented
separately below. Copilot Studio and Teams configuration remain later work.

### HTTP MCP Stages 2-3

`src\WorkMonitor.Mcp.Http` is a **separate ASP.NET Core process** using the
official `ModelContextProtocol.AspNetCore` **2.2.0** Streamable HTTP transport
on .NET 10. It maps **POST `/mcp`** only, in explicitly stateless mode. It does
not serve the FT-MONITOR web/dashboard, static assets, web controllers, cookies,
desktop credentials, or a browser login. No cross-request user tokens, ticket
results, MCP sessions, or replayable response caches are persisted.

Exactly four tools are registered and advertised:

| Tool | Remote contract |
|---|---|
| `get_work_item` | Positive integer `id`; optional `view` is `summary` (remote default) or `detail`. Uses the shared Stage 1 implementation, links, revision, bounds and coverage. |
| `analyze_feedback_themes` | Configured `projectArea` OR saved-query alias/GUID `query`; optional `maxResults` defaults to 20, range 1-50. Shared grounded analysis, canonical ticket links and explicit source/window/coverage. |
| `list_saved_queries` | Host-owned configured catalog only; does not execute queries. |
| `list_project_areas` | Configured area keys and paths. |

This allowlist is enforced both by registration and a request preflight.
Manual calls to write tools (including dry runs), `execute_saved_query`,
or any other name are rejected before OBO, downstream or model work.
`WorkMonitor:Mcp:AllowWrites` and `confirm` cannot enable remote writes.
Remote themes reuse Stage 1's service, classifier, parser and read budget;
they never call FT-MONITOR web. The desktop stdio host retains
its existing 16 tools and detailed lookup default.

#### Authentication and confidential-client configuration

Use an approved **single-tenant Entra API application**, with v2 access tokens
(`api.requestedAccessTokenVersion: 2`). The host validates signature (RS256
using tenant HTTPS signing metadata), exact tenant-specific v2 issuer,
audience, tenant, lifetime, nonempty GUID `oid`, and the configured delegated
`scp` on **every request**, including MCP initialization/discovery. For this
v2 API, the expected `aud` is the application's **ClientId GUID**, not the
Azure DevOps resource or the full scope URI. The authority is fixed to
`https://login.microsoftonline.com/<TenantId>/v2.0`; there is no
`common`/multi-tenant or arbitrary identity-provider fallback. App-only tokens,
roles without the delegated scope, wrong-tenant, expired, and anonymous
requests are denied.

The client must obtain an **access token for this API** with the exposed
scope `api://<ClientId>/<RequiredScope>` (for example `access_as_user`) and
send `Authorization: Bearer <API-access-token>` on each MCP HTTP request.
The host does not implement an OAuth authorization server, dynamic client
registration, or interactive sign-in; use a client with approved Entra OAuth
support/configuration. It never accepts ID tokens or forwards the API token
directly to DevOps. `X-WorkMonitor-DevOps-Token` overrides are rejected.

For ticket lookup and theme source reads, the validated request's API token is the user assertion for
MSAL `AcquireTokenOnBehalfOf`, using this API's confidential-client credential
and the fixed DevOps resource scope
`499b84ac-1321-427f-aa17-267ca6975798/.default`. MSAL adds its standard OpenID
scopes. Only the returned downstream access token goes to DevOps; Microsoft
service tokens are treated as opaque, not parsed as this API's JWT contract.
There is no shared maker identity, client-credentials grant, managed-identity
fallback, interactive server sign-in, or saved desktop token cache.
Each OBO acquisition uses a fresh confidential client with a request-local
cache. Pooled identity-service, DevOps and model HTTP handlers disable cookies
and automatic redirects, so response cookies cannot carry state across users.
Catalog discovery needs no downstream token but still requires API
authentication. Incoming `Mcp-Session-Id` headers are rejected; clients must
not attempt to reuse state from another request/user.

Identity, credential and catalog settings are under **`WorkMonitor:HttpMcp`**:

| Setting | Requirement |
|---|---|
| `TenantId` | Single permitted tenant GUID. |
| `ClientId` | API application/client GUID; also the required token audience. |
| `RequiredScope` | One exposed delegated scope name, e.g. `access_as_user`. |
| `DataDirectory` | Absolute host-owned catalog directory; do not point at a desktop account/cache directory. Existing `data\parameters.json` and `data\saved-queries.json` use the shared catalog format/precedence. |
| `CertificatePath` | **Preferred:** absolute path to a mounted PFX with a currently valid RSA certificate/private key (at least 2048 bits) registered for this API. Loaded into ephemeral key storage; no key installation is performed. |
| `CertificatePassword` | Optional mounted-PFX password from the approved secret provider. |
| `ClientSecret` | Explicit alternative only when policy permits; supply from an approved vault/secret injection, **instead of** `CertificatePath`. |

Exactly one credential must be configured. Missing/invalid configuration,
unreadable/invalid certificates, or conflicting credentials fail startup.
A rejected credential/consent at Entra fails the request explicitly, never
falls back. Certificates/secrets and passwords must not go into checked-in
JSON, command arguments, screenshots, logs or client configuration. Certificate
rotation and secret injection remain host/operator responsibilities; neither
credentials nor tenant permissions are provisioned by this code.

The standard **top-level `AllowedHosts`** setting is a semicolon-separated
list of **exact host names**, without schemes or ports. Its safe local default
is `localhost;127.0.0.1;[::1]`. Production operators must replace it with the
approved exact service host names, for example `mcp.example.invalid`, rather
than opening it to arbitrary hosts. Wildcards (`*`, `*.example.invalid`),
unspecified-address wildcards (`0.0.0.0`, `[::]`), empty entries, array
configuration, schemes and ports fail startup. Use ASCII/punycode DNS names;
IPv6 literals must be bracketed and normalized. Host ports are handled
separately by the HTTPS listener.
ASP.NET Core Host Filtering rejects unapproved or empty `Host` values before
authentication, OBO or downstream work. This is explicit host configuration,
not protection automatically supplied by MCP; see the SDK's
[Host name validation guidance](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/transports/transports.html#host-name-validation).

This host remains **server-to-server only**: every `Origin`-bearing request is rejected,
including same-origin, foreign, malformed, empty and literal `null` values.
No browser-origin allowlist, CORS policy or browser integration is enabled.
Copilot/service clients must omit `Origin`; valid no-Origin clients still
require the normal per-request API token and HTTPS.

Build/run from the isolated checkout with the SDK pinned by `global.json`:

```powershell
$dotnet = (Get-Command dotnet -CommandType Application).Source
& $dotnet build src\WorkMonitor.Mcp.Http\WorkMonitor.Mcp.Http.csproj -c Release

$env:WorkMonitor__HttpMcp__TenantId = "YOUR-TENANT-GUID"
$env:WorkMonitor__HttpMcp__ClientId = "YOUR-API-APPLICATION-GUID"
$env:WorkMonitor__HttpMcp__RequiredScope = "access_as_user"
$env:WorkMonitor__HttpMcp__DataDirectory = "C:\WorkMonitorHttpData"
$env:WorkMonitor__HttpMcp__CertificatePath = "C:\MountedSecrets\workmonitor-obo.pfx"
# CertificatePassword, if needed, comes from approved secret injection.
# Configure the host's approved HTTPS server certificate separately from OBO.
$env:ASPNETCORE_URLS = "https://localhost:7443"
# Local AllowedHosts defaults already cover localhost. For an approved deployment:
# $env:AllowedHosts = "mcp.example.invalid" # exact actual host names; no wildcard
& $dotnet run --no-launch-profile --project src\WorkMonitor.Mcp.Http -c Release
```

These are placeholder setup instructions, **not an authorization to register
an app, obtain credentials, grant consent, trust a TLS certificate or deploy**.
No new registration/permission changes are needed just to run deterministic
tests. In an activated environment, use approved HTTPS hosting and a trusted
server certificate. Plain HTTP is rejected, not redirected. If using a TLS
terminator, use an approved platform integration that securely establishes the
upstream HTTPS scheme; the app does not trust arbitrary forwarded headers.
The ingress must also validate the exact public host names, particularly if it
rewrites `Host` before forwarding; never trust an unrestricted forwarded host.
Apply host/network access restrictions and appropriate request/concurrency
limits. The application bounds inbound requests to 64 KiB and does not enable
CORS, legacy SSE endpoints, or response caching. Never bypass TLS warnings.

After MCP initialization, the authenticated HTTP `tools/call` body is:

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tools/call",
  "params": {
    "name": "get_work_item",
    "arguments": { "id": 12345 }
  }
}
```

Use `Content-Type: application/json`, accept both `application/json` and
`text/event-stream`, and send the negotiated `MCP-Protocol-Version` header.
The default summary does one projected read, not description/comment/customer
fetches. An explicit `{"id":12345,"view":"detail"}` retains Stage 1's bounded
detail/coverage contract. No URL-fetch input is supported. Required HTTP
authentication is independent of the desktop stdio initialization.

OBO interaction/Conditional Access failures are handled **before** MCP starts
streaming: HTTP **401** carries a tenant-specific `WWW-Authenticate` challenge,
with `insufficient_claims` and a bounded base64-encoded JSON claims request
when Entra supplies one. Otherwise, a sanitized sign-in/consent error is
returned. Clients must reacquire an API token using the challenge and retry
only after sign-in/approved consent, not spin on the same token. Credential,
identity-service and source failures use sanitized actionable errors; no raw
identity error body, assertion, secret or denied-ticket body is returned.
Cancellation propagates through OBO and downstream reads.

#### Stage 3 server catalogs and model configuration

The HTTP process does **not** deploy or read a desktop user's catalogs.
Operators must provision host-owned files/configuration under the configured
`WorkMonitor:HttpMcp:DataDirectory` independently. Shared precedence is:

| Catalog | Precedence |
|---|---|
| Saved queries | `<DataDirectory>\data\saved-queries.json` (including an empty dictionary) overrides `AzureDevOps:SavedQueries` configuration completely. Without that file, configuration provides the catalog. Aliases resolve case-insensitively; a direct GUID also works and is still authorized by user OBO. |
| Areas | A nonempty `<DataDirectory>\data\parameters.json` dictionary overrides the built-in area mapping. Missing/empty dictionaries fall back to built-in areas; this fallback is not evidence that an area is approved or accessible to a pilot user. There is no `AzureDevOps:AreaPaths` configuration binding. |

For example, provision `data\saved-queries.json` with the **actual approved**
pilot query GUID replacing this placeholder:

```json
{ "FTMonitor - FTS": "REPLACE-WITH-APPROVED-SAVED-QUERY-GUID" }
```

Or, without that file, configure
`AzureDevOps__SavedQueries__0__name=FTMonitor - FTS` and
`AzureDevOps__SavedQueries__0__queryId=<approved-query-GUID>`.
No FTS GUID is embedded in the code. A corresponding `data\parameters.json`
example is:

```json
{ "Project": "ExampleProject\\Feedback", "Pilot": "ExampleProject\\Feedback\\Pilot" }
```

These are deployment examples, not resource/permission changes. Invalid JSON
or inaccessible catalog files fail explicitly when the catalog is loaded;
unknown/misconfigured aliases and area keys fail validation before OBO/model
work. Do not copy a desktop account/cache directory into the host. Catalogs
are loaded by the process, not automatically refreshed or edited by MCP.
Discovery lists configured values, not proof of the caller's query access.

The model uses a **separate server identity**, never the incoming API token,
DevOps OBO token, browser identity or persisted developer login.
`AzureOpenAI` settings in the HTTP host:

| Setting | Requirement |
|---|---|
| `Endpoint` | Approved absolute HTTPS origin, e.g. `https://YOUR-RESOURCE.openai.azure.com/`; no path, query, user info or fragment. |
| `DeploymentName` | Existing approved chat deployment; letters/digits, `.`, `_`, `-`, at most 128 characters. |
| `Authentication` | Explicit `ManagedIdentity` (checked-in default) or `ApiKey`. No `DefaultAzureCredential`, developer CLI/browser credential chain or credential fallback. |
| `ManagedIdentityClientId` | Optional user-assigned managed-identity client GUID; empty means system-assigned. Only valid with `ManagedIdentity`. |
| `ApiKey` | Explicit approved alternative injected through the host's secret provider, only with `Authentication=ApiKey`. Never put keys in checked-in config, requests or logs. |

For managed identity, the host operator must separately approve/enable that
identity and its **Cognitive Services OpenAI User** access on the model resource.
The host itself creates/grants nothing. Model and model-identity clients
disable redirects, cookies and HTTP client request logging. Model calls use
the existing production Azure OpenAI SDK serialization with **zero automatic
model retries**. Managed-identity acquisition also disables automatic retries.
The host binds model identity acquisition to the request deadline explicitly:
the pinned Azure OpenAI SDK's authentication policy otherwise passes a default
cancellation token. Only the server model identity token can reach that policy.
Shared non-model tools, initialization and discovery do not resolve model
credentials/settings. Missing/invalid model or Themes policy configuration
produces an actionable **HTTP 503 `theme_configuration_required`** only when
themes are invoked; it does not break lookup or startup.

After normal authenticated MCP initialization:

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "tools/call",
  "params": {
    "name": "analyze_feedback_themes",
    "arguments": { "query": "FTMonitor - FTS", "maxResults": 20 }
  }
}
```

Alternatively use `{"projectArea":"Pilot","maxResults":20}`. Omitting both
selects the configured `Project` key. Do not send both sources, explicit
nulls, duplicate/unknown arguments, tokens, credentials or URLs. A source
window makes one bounded query read (`maxResults+1` lookahead) and at most one
batch read under the requesting user's OBO; no discussion fetches. Empty,
missing-only, excluded-only or denied sources make **no model call**. Eligible
data makes at most one classifier call. The official SDK's stateless
`ConfigureSessionOptions` hook runs theme preflight **after SDK HTTP transport
validation and before SSE**. Invalid Accept, Content-Type, protocol-version or
required MCP headers/metadata cannot consume a theme rate-window admission,
OBO, source or model call. Missing/non-JSON Content-Type returns HTTP 415;
the SDK remains authoritative for accepted negotiation/version forms, including
its omitted-version fallback. SDK tool dispatch consumes only that request's
preflight result; there is no cross-request ticket/result cache or asynchronous
job workflow.

Results retain Stage 1's `themes`, `items`, `source` and `coverage`, including
source selection/window, observed versus unknown total, retrieved/missing/
excluded IDs, analyzed/classified counts, unclassified IDs and payload/text/
output omissions. Saved-query order is preserved and non-feedback items are
excluded, not replaced. Canonical ticket links refer only to retained source
IDs. Themes are groupings, **not confirmed duplicates**. Model/ticket text is
untrusted data, never instructions. A truncated FTS window is not exhaustive.

#### Theme limits, deadlines and errors

`WorkMonitor:HttpMcp:Themes` settings are validated lazily for theme calls:

| Setting | Default | Supported range |
|---|---|---|
| `TimeoutSeconds` | 60 | 1-120 |
| `RequestsPerWindow` | 2 | 1-10 |
| `WindowSeconds` | 60 | 1-300 |
| `ConcurrentPerUser` | 1 | 1-2 |

The in-process, no-queue limiter partitions by **validated tenant + normalized
user oid**, never a request-supplied identity. An attempt admitted to expensive
work counts toward its fixed window even if it later fails/cancels. Capacity
leases release on success/error/cancellation; rate windows refill separately.
Throttled requests receive **HTTP 429 `theme_rate_limit` with `Retry-After`**
before OBO/source/model calls. Catalog tools and lookup are not subject to
the theme limiter. Limits are per host process; approved ingress-level limits
are still needed for a multi-instance deployment.

One end-to-end deadline covers OBO, source retrieval and model completion.
Cancellation propagates to all three; a deadline/downstream timeout returns
**HTTP 504 `theme_analysis_timeout`**, never a successful partial result.
The host does not silently drop slow calls or retry the model. OBO claims
challenges still return HTTP 401 before streaming. Source permission/service
errors return sanitized **HTTP 502 `theme_source_unavailable`**; malformed,
ungrounded, over-limit or failed model/source results return sanitized
**HTTP 502 `theme_analysis_failed`**, without raw errors/tokens/ticket bodies.
Invalid source selections/arguments return JSON-RPC `-32602` before expensive
work. Clients should not blindly retry errors or reissue completed analyses.

Inbound MCP remains limited to **64 KiB**. Remote DevOps responses are bounded
to **2 MiB** and model response envelopes to **512 KiB**, including chunked
bodies. The shared classifier payload is at most **100,000 serialized
characters**, with title/description/state limits 256/2,000/80. Generation is
bounded to **8,192 output tokens**; parsed model text is limited to 100,000
characters. Tool results fit **14,000 serialized characters**, preserving
explicit omissions/partial coverage rather than slicing JSON. Exceeding a
response limit fails explicitly.

#### HTTP-only publish artifact and operator preflight

The existing App Service workflow, `deploy-appservice.ps1` and portable web
publisher target **FT-MONITOR/dashboard**, not this host. The desktop publisher
targets CLI/stdio. Do not reuse those deployment commands or their application
names for HTTP MCP. No approved HTTP-host deployment target or public URL is
established here; the following prepares a host-neutral artifact only.

The full solution now targets `net10.0`, pinned to GA SDK **10.0.404** with
roll-forward disabled and no prerelease SDKs. The selected .NET and ASP.NET Core
runtime patch is **10.0.15** (November 10, 2026 security release); use that or a
later supported .NET 10 servicing patch on the target. Microsoft's
[release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
and [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
list .NET 10 as active LTS through **November 14, 2028**. Do not force
major-version runtime roll-forward.

The earlier `artifacts\workmonitor-http\f94c60fbbde4` directory and
`artifacts\workmonitor-http-f94c60fbbde4.zip` still contain **.NET 8** bits and are
**superseded, not suitable for live activation** (.NET 8 ended support November
10, 2026). Keep those reviewed artifacts unchanged. The new
`net10-local-migration-f94c60fbbde4` label explicitly identifies the uncommitted
migration on top of that commit, not a build of the commit alone.

From a reviewed checkout with its dependency graph already restored, publish
only the HTTP project into a **fresh** ignored artifact directory:

```powershell
$dotnet = (Get-Command dotnet -CommandType Application).Source
$revision = (git rev-parse HEAD).Substring(0, 12)
$label = "net10-local-migration-$revision" # migration remains uncommitted
$artifact = Join-Path (Get-Location) "artifacts\workmonitor-http\$label"
if (Test-Path -LiteralPath $artifact) {
    throw "Do not overwrite a reviewed artifact; use a fresh output directory."
}
& $dotnet publish src\WorkMonitor.Mcp.Http\WorkMonitor.Mcp.Http.csproj `
    --configuration Release --no-restore --self-contained false `
    -p:UseAppHost=false -p:IsTransformWebConfigDisabled=true `
    -p:DebugType=None -p:DebugSymbols=false --output $artifact
if ($LASTEXITCODE -ne 0) { throw "HTTP host publish failed." }
```

This is framework-dependent: the target needs the matching **ASP.NET Core 10
shared runtime** (10.0.15 or a later supported 10.0 patch), not just the base
.NET runtime. Verify `dotnet --list-runtimes` before launch. It includes the HTTP DLL,
`WorkMonitor.Agent`/`WorkMonitor.Core` and their runtime dependency closure
(including shared SQLite/DevOps SDK libraries), not the desktop executables or
FT-MONITOR app. No apphost, IIS `web.config`, dashboard/static assets, test
assemblies, desktop token/cache/database files, real catalogs, PFX files or
secrets belong in the artifact. Inspect the actual output, not merely the
project name. The only application configuration shipped is the checked-in
placeholder `appsettings.json`; never package a locally populated copy.
Archive/hash this reviewed directory separately from any mounted data/secrets.
Do not overlay a dashboard/desktop publish directory or copy a user's home.
The unchanged legacy DevOps SDK retains `System.Data.SqlClient` 4.8.5; .NET 10's
transitive dependency audit reports **NU1903**
([advisory](https://github.com/advisories/GHSA-98g6-xh36-x2p7)).
The runtime migration does not remediate that dependency; review it separately
before production approval.

The operator must supply the **approved** hosting target and exact public
hostname, trusted TLS server certificate, permitted ingress/backend addresses,
supported runtime, Entra tenant/API/scope/consents and OBO credential, pilot
catalogs and independent model identity/deployment. None is provisioned by
publishing. After runtime verification and approvals are satisfied, the launch
shape below uses operator-provided values; it is not a deployment command:

```powershell
# On the approved host, using its runtime path and extracted artifact directory.
$artifact = "C:\ApprovedApps\WorkMonitorHttp"
$dotnet = "C:\ApprovedDotnet\dotnet.exe"
$env:DOTNET_ENVIRONMENT = "Production"
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_FORWARDEDHEADERS_ENABLED = "false"
$env:AllowedHosts = "mcp.example.invalid" # replace with exact approved DNS name
$env:WorkMonitor__HttpMcp__TenantId = "YOUR-TENANT-GUID"
$env:WorkMonitor__HttpMcp__ClientId = "YOUR-API-APPLICATION-GUID"
$env:WorkMonitor__HttpMcp__RequiredScope = "access_as_user"
$env:WorkMonitor__HttpMcp__DataDirectory = "C:\MountedCatalogs\WorkMonitorHttp"
$env:WorkMonitor__HttpMcp__CertificatePath = "C:\MountedSecrets\workmonitor-obo.pfx"
$env:WorkMonitor__HttpMcp__ClientSecret = "" # certificate mode; never both
# OBO CertificatePassword, if needed, is injected by the approved secret provider.

# Loopback suits an ingress on this host. A different private bind needs approval.
$listenAddress = "127.0.0.1"
$env:Kestrel__Endpoints__Mcp__Url = "https://${listenAddress}:7443"
$env:Kestrel__Endpoints__Mcp__Certificate__Path = "C:\MountedSecrets\workmonitor-https.pfx"
# Kestrel__Endpoints__Mcp__Certificate__Password is separately secret-injected.
$env:AzureOpenAI__Endpoint = "https://YOUR-RESOURCE.openai.azure.com/"
$env:AzureOpenAI__DeploymentName = "YOUR-APPROVED-DEPLOYMENT"
$env:AzureOpenAI__Authentication = "ManagedIdentity"
$env:AzureOpenAI__ManagedIdentityClientId = "" # or approved user-assigned GUID
$env:AzureOpenAI__ApiKey = "" # explicit approved key mode is documented above
& $dotnet (Join-Path $artifact "workmonitor-mcp-http.dll") --contentRoot $artifact
```

Keep the TLS server certificate and the API's OBO client certificate separate.
Passwords, the optional OBO secret and optional model key are never shell
arguments or values in this example. Run under the operator's service identity
with least-privilege read access to code, mounted secrets and catalogs.
Precreate `<DataDirectory>\data` and mount its `parameters.json` and
`saved-queries.json` using the formats/precedence above. The shared catalog
loader calls `Directory.CreateDirectory`; precreating the directory allows a
read-only catalog mount. No MCP tool edits those files.

For this host-neutral form, use direct trusted HTTPS or **HTTPS re-encryption
from ingress to Kestrel**, with ingress preserving the approved public Host.
Validate the public Host at ingress too, restrict backend access to that
ingress, configure upstream certificate/SNI validation, and never disable TLS
verification. Binding an address does not add it to `AllowedHosts`. If ingress
must rewrite Host, explicitly approve/allow only the resulting backend Host
and enforce the original public Host at ingress. Do not enable wildcard hosts
or arbitrary forwarded-host trust to make a probe pass.

There is no custom trusted-proxy/forwarded-header configuration in this
artifact. Keep `ASPNETCORE_FORWARDEDHEADERS_ENABLED=false`; a plaintext upstream
does not become HTTPS by sending `X-Forwarded-Proto: https`. An HTTP-only TLS
termination arrangement needs a separately reviewed, explicitly trusted
platform/proxy integration before it can be used, not an unrestricted header
switch. Do not repurpose the existing web workflow or turn on CORS.

There is no anonymous health/UI route. Use an approved network/TLS health probe
or an appropriately authenticated probe, not a bypass around MCP authorization.
After actual activation, an anonymous request can safely verify the auth
boundary without obtaining a token or reading tickets:

```powershell
$endpoint = "https://mcp.example.invalid/mcp" # actual approved URL
curl.exe --silent --show-error --output NUL --write-out "%{http_code}`n" `
    --request POST $endpoint --header "Content-Type: application/json" `
    --header "Accept: application/json, text/event-stream" `
    --data '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
# Expected 401; TLS/transport failures are errors, never bypass with --insecure.
```

Then use the intended approved OAuth/MCP client to verify allowed/denied users,
401 claims-challenge handling, exact four-tool discovery, catalog contents,
canonical ticket fields/links, permitted and denied source access, and FTS/area
themes with honest coverage. With a valid user, unapproved Host/Origin and
plaintext HTTP must remain denied; forwarded headers must not elevate HTTP.
Measure end-to-end theme latency and error/throttle behavior through the actual
connection before acceptance. A local artifact/startup check cannot establish
public TLS trust, tenant consent, source/model access or connector latency.

#### Verification versus activation gates

Deterministic tests exercise actual JWT signature/issuer/audience/lifetime
validation and HTTP MCP initialization/discovery/calls. Controlled OBO and
DevOps boundaries cover two interleaved/concurrent users, production MSAL's
OBO form and certificate/secret paths, claims challenges, cancellation,
disallowed tools and malformed/denied tickets. They make **no live DevOps or
paid model calls**. They do **not** prove tenant consent or production access.
HTTP fixtures also cover rejected spoofed hosts and Origin-bearing requests
with valid user tokens, wildcard/invalid host configuration at startup, and
successful no-Origin calls on loopback and an explicitly configured service host.
Stage 3 fixtures exercise actual HTTP theme calls and production model SDK
requests with fake OBO/DevOps/model endpoints, catalog precedence, strict
arguments, SDK transport rejection before expensive work, supported header/
version alternatives, grounding, bounded payloads/errors, no retry, two-user
isolation, throttling/recovery and deadlines/cancellation. These are deterministic
behavior checks, **not measured connector latency or live model/identity access**.

Before activation, the application owner/admin must separately approve and
verify the API scope/client consent, confidential-client credential, Azure
DevOps delegated `user_impersonation` permission and downstream OBO consent,
and tenant/Conditional Access policy. A deployed, trusted-HTTPS smoke test must
then prove that a permitted real user can read a permitted ticket and that a
denied user cannot. These are **remaining live activation gates**, along with
approved hosting/network controls. Stage 3 activation additionally requires
the actual pilot FTS query/area catalog, user query/ticket access, approved
model identity/key/deployment and resource/network access. Measure real
end-to-end tool latency and verify the four-tool discovery using the intended
Copilot Studio connection before pilot acceptance. The documented
[five-minute model-response streaming limit](https://learn.microsoft.com/en-us/microsoft-copilot-studio/requirements-quotas)
does **not** cover orchestration or tool/connector execution and does not
establish compatibility with this host's deadline. Connector-specific limits
must be checked live; [MCP troubleshooting guidance](https://learn.microsoft.com/en-us/microsoft-copilot-studio/mcp-troubleshooting)
also notes discovery changes can take approximately 12-24 hours after custom
connector republishing. No such setup, publication or live measurement was
performed here. Copilot Studio wiring and Teams publication remain later
stages. This dedicated host does not publish or harden the existing dashboard.

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

- GA .NET SDK **10.0.404**, pinned by `global.json`; all seven solution projects
  target `net10.0`. Use `dotnet --version` from the checkout to verify SDK
  selection. Do not use an older SDK-only installation or major roll-forward.
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
