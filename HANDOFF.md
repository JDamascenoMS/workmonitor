# WorkMonitor conversation handoff

Updated: 2026-10-14.

## Repository and PR scope

- Repository: https://github.com/JDamascenoMS/workmonitor (private).
- Local checkout: `C:\arq\monitors\workmonitor`.
- Original application: `JDamascenoMS/FT-MONITOR-1`, separately cloned under
  `C:\arq\monitors\FT-MONITOR-1`; it has not been modified by this work.
- Existing default branch: `main`.
- Web milestone commit: `021d76597381ac416843b79a8fb812484acf3177`,
  already pushed to `main`.
- Earlier commits: `685b4a1` (CLI/shared runtime) and `92bd613`
  (expanded read tools/versioned releases).
- The user requested a PR merged into `master`. There was no `master` in
  WorkMonitor. The PR targets a new `master` based on the pre-web baseline
  `92bd61367f12b81eea875ac34418091b99739584`, so its diff includes the web
  milestone and this handoff. This does not rewrite `main` or change the
  repository's default branch.

## What the project does

WorkMonitor is an engineering-work assistant over Azure DevOps and GitHub.
Shared Core and Agent libraries serve a conversational CLI, an MCP stdio
adapter, and an ASP.NET Core web application.

- The application's own agent orchestrates model/tool calls. It is not an
  Azure AI Foundry-managed agent.
- CLI/MCP expose 13 tools: 12 read tools and one gated comment-write tool.
- The web agent exposes only the 12 read tools and rejects live write mode.
- Read surfaces include assigned items, mentions, saved queries, review queues,
  recent activity, GitHub issues, and evidence-based cross-source correlation.
- CLI conversations and audit records use SQLite. The web now reuses shared
  SQLite storage with SQL-enforced tenant/object-ID ownership.
- Web chat supports NDJSON streaming, persisted follow-up context, tool
  outcomes, cancellation, turn concurrency protection, and older-chat paging.

See [README.md](README.md) for usage and registration details.

## Model backend and credential handling

The locally configured model backend is:

- Azure OpenAI resource: `https://wte-ai-jdamasceno.openai.azure.com/`.
- Deployment: `model-router`.
- SDK: Azure.AI.OpenAI/OpenAI 2.1.0. The default stable API `2024-10-21`
  was verified against this deployment.
- Model API key: local .NET user-secrets only, not committed.

The model key was previously shared in chat. Rotation remains recommended.
Never reproduce it in a handoff, PR, log, source file, or automation prompt.
The model key cannot authenticate Microsoft sign-in or Azure DevOps.

The web, CLI, and MCP hosts use the existing user-secrets ID
`2243d00b-0139-435a-8d18-5fbfb023e1a5`.
Developer settings and credential caches remain outside Git.

## Authentication: two distinct web modes

### Default: SpaPkce

This is the intended per-user web experience.

- Supported, self-hosted MSAL Browser 5.25.0 authorization code flow with PKCE.
- Entra client ID: `7e3ce349-738d-45e3-ae34-9fdae87bea92`.
- Tenant ID: `72f988bf-86f1-41af-91ab-2d7cd011db47`.
- SPA callback: `https://localhost:7286/auth-callback.html`.
- Own API scope:
  `api://7e3ce349-738d-45e3-ae34-9fdae87bea92/access_as_user`.
- Azure DevOps delegated scope:
  `499b84ac-1321-427f-aa17-267ca6975798/user_impersonation`.
- API authentication uses an own-resource access token with validated
  signature, issuer, audience, expiry and delegated scope. ID tokens,
  application-only tokens and DevOps resource tokens cannot authenticate it.
- The browser separately forwards a DevOps token only for operations that
  need it. The server treats that token as opaque and verifies the account
  using DevOps connection data and Graph, matching its tenant/object IDs to
  the API caller.
- No cookie-auth or host-identity fallback. No client secret or application
  certificate is required for this mode.

The user initially encountered `AADSTS500011` because API registration/scopes
were incomplete. After adding a scope, sign-in reached "Need admin approval"
for Ft-Monitor-Entra. The exact blocked permission(s) have not been confirmed.
The earlier screenshot showed an admin-only scope and no preauthorized
clients. Own-API consent and Azure DevOps consent are separate requirements.
The user cannot obtain tenant administrator approval at present.

### Explicit alternative: LocalDevelopment

The user approved this mode for a local demonstration. It is enabled only
in local user-secrets via `WorkMonitor:Web:AuthenticationMode`.

- Startup requires the Development environment and loopback listeners.
- Raw client IP and Host must be loopback; proxy headers are ignored.
- Basic original-style OIDC requests only `openid profile email`, using the
  Web callback `https://localhost:7286/signin-oidc`.
- OIDC sign-in and subsequent cookie validation require the browser's verified
  tenant/object IDs to match the developer's cached Azure/DevOps identity.
- DevOps uses the same tenant/account-pinned protected credential cache as
  the CLI, with interactive authentication disabled in the web host.
- Missing/expired cached authentication gives an actionable error instructing
  the developer to run the CLI's `--auth-login`. It does not open a browser
  from background web requests or select another host identity.
- Secure, HttpOnly, mode-specific cookies; data-protection keys outside source
  and protected with DPAPI on Windows.
- All unsafe MVC API requests require CSRF, including parameters and skills.
- The UI displays a development warning and the account used for DevOps.
- Local sign-out clears only the web cookie, not the CLI credential cache.
- Read-only tools remain enforced. This is not a production/shared-host mode.

Basic sign-in still requires the tenant to permit the original application
and its Web redirect/ID-token configuration. This alternative does not
bypass tenant policy.

### Why the original application behaved differently

The original FT-MONITOR used OIDC to authenticate pages, but used
DefaultAzureCredential for DevOps (managed identity in Azure, developer
credentials locally). DevOps operations were not tied to the browser user.
The first web milestone intentionally introduced per-user delegated access,
which exposed the confidential-client credential prerequisite.

The tenant blocks client secrets and rejected even a 24-hour development
application-auth certificate. Those certificates were kept outside Git;
they were not uploaded successfully or configured in WorkMonitor. The
24-hour test certificate has expired. Do not keep generating certificates
or weaken tenant policy.

## Verification completed

- 248 targeted Release tests passed on 2026-10-14, including both web auth
  modes, ownership, streaming/follow-ups, cancellation, loopback/CSRF guards,
  credential cache-only behavior, shared session-store compatibility and MCP.
- Browser checks used Edge and the actual MSAL SDK, plus isolated regressions
  for token isolation, callback handling, local mode and popup close races.
- Vendor SDK/license SHA-256 hashes match the pinned provenance. Git attributes
  preserve exact SDK bytes on Windows checkout for browser integrity checks.
- Local HTTPS chat and auth metadata return 200.
- Anonymous protected APIs return 401; development requests with non-local
  Host are rejected with 403.
- The live development OIDC redirect was inspected and requests only
  `openid profile email`, with `response_type=id_token`.
- A real MCP `list_my_work_items` call succeeded on 2026-10-14 and reported
  398 active assigned DevDiv items. No work items were modified. Internal
  item titles/IDs are intentionally omitted from this repository handoff.

Not yet verified: actual browser OIDC sign-in followed by a real streamed
DevOps request, follow-up and reload/resume. Unit/integration tests and a
successful MCP call are not proof of that browser end-to-end flow.

## Local environment and useful commands

- Windows; SDK 8.0.424 is pinned by [global.json](global.json).
- .NET host: `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`.
- User prefers Microsoft Edge, not an integrated browser.
- GitHub CLI is installed under the current user's WinGet package directory.
- Azure CLI was not installed when last checked.

```powershell
Set-Location 'C:\arq\monitors\workmonitor'
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build '.\FT-MONITOR.sln' --configuration Release --no-restore
& $dotnet test '.\tests\WorkMonitor.Tests\WorkMonitor.Tests.csproj' `
    --configuration Release --no-restore `
    --filter 'FullyQualifiedName~LocalDevelopmentAuthenticationTests|FullyQualifiedName~WebAuthenticationTests|FullyQualifiedName~WebBearerAuthenticationTests|FullyQualifiedName~WebConversationTests|FullyQualifiedName~PersistentInteractiveCredentialTests|FullyQualifiedName~OwnedSessionStoreTests|FullyQualifiedName~SessionStoreTests|FullyQualifiedName~McpProtocolTests'
& $dotnet '.\src\WorkMonitor.Cli\bin\Release\net8.0\workmonitor.dll' --check
```

Use `--auth-login` instead of `--check` when the developer explicitly needs
to renew the CLI authentication cache. Do not log tokens or dump user-secrets.

The MCP host is launched without additional arguments:

```powershell
& $dotnet '.\src\WorkMonitor.Mcp\bin\Release\net8.0\workmonitor-mcp.dll'
```

Transport is stdio, not the web preview URL. Set
`WorkMonitor__Mcp__AllowWrites=false` and allowlist read-only tools in the
client. `--list-tools` lists names and exits; it is not a server startup flag.
The MCP host can still recover interactively by default, unlike local web
authentication; scheduled execution needs an explicit cache-only option.

### Preview and persisted data

- Preview: `https://localhost:7286/agent.html`; HTTP port 5191.
- Owned detached process identifier in this conversation: `workmonitor-web`.
- Launch from `FT-MONITOR` with `ASPNETCORE_ENVIRONMENT=Development` and
  `ASPNETCORE_URLS=https://localhost:7286;http://localhost:5191`.
- Stop that owned process before rebuilding locked DLLs on Windows and
  restart it afterward. Never kill unrelated processes by name.
- CLI data defaults to `%LOCALAPPDATA%\WorkMonitor`.
- Web chats: `%LOCALAPPDATA%\WorkMonitor\Web\conversations.db`.
- Development cookie keys: its `LocalDpKeys` directory.
- Current storage and turn coordination target one app instance.

## Recurring reports and Copilot product distinction

The user wants a daily DevDiv report from Copilot Automations.

- GitHub Copilot app/CLI/VS Code can use the existing local stdio MCP server.
  GitHub Copilot app local automations are a possible integration, but no
  WorkMonitor MCP registration or schedule has been configured by this work.
- The user's actual screenshot showed Microsoft Copilot **Cowork**
  (Chat / Cowork / Code tabs), not the GitHub Copilot app interface.
  Earlier "Customize -> MCP" instructions referred to the wrong product
  and were corrected.
- Cowork uses plugins/connectors; its documented connectors call remote
  servers. The current WorkMonitor stdio host is not a ready-to-upload
  Cowork plugin or authenticated remote MCP service.
- A Cowork integration requires an organization-approved remote endpoint,
  authentication/authorization design, plugin packaging/registration, and
  testing before an automation can be scheduled. The Add dropdown options
  in the user's UI have not yet been inspected.
- A skill containing reporting instructions does not itself grant DevDiv
  access. A cloud run cannot automatically reuse the Windows developer cache.

Do not export internal DevDiv data to GitHub issues/commits or third-party
endpoints without organizational approval. Daily reports should identify
retrieval limits, unavailable sources, timestamps and identity, and must
not modify work items.

Sources:

- [GitHub Copilot app automations](https://docs.github.com/en/copilot/how-tos/github-copilot-app/using-automations)
- [GitHub Copilot CLI MCP configuration](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers)
- [Microsoft Cowork plugin development](https://learn.microsoft.com/en-us/microsoft-365/copilot/cowork/cowork-plugin-development)

## Deployment and next actions

1. Verify real localhost browser sign-in with the cached developer account,
   then "Show my active work items", follow-up, cancellation and reload.
2. Keep SPA/per-user production auth blocked until approved Entra registration
   and consent are available; retain fail-closed behavior.
3. Choose the reporting host: local GitHub Copilot tooling versus a properly
   approved remote Cowork connector. Harden unattended MCP authentication
   before scheduling. No automation currently exists.
4. Rotate the exposed model API key using the approved secret store.
5. Decide separately whether `master` should become the default branch.
   The inherited deployment workflow triggers only pushes to `main`; it does
   not run on this master-targeted PR. Its Azure login previously failed due
   to missing deployment variables/approved identity. No Azure deployment or
   workflow policy change is included in this handoff.
6. Multi-instance hosting requires shared conversation storage and distributed
   turn coordination. Do not deploy LocalDevelopment mode remotely.

Important code entry points:

- [Web host/auth wiring](FT-MONITOR/Program.cs)
- [Conversation and streaming APIs](FT-MONITOR/Controllers/AgentController.cs)
- [Development authentication](FT-MONITOR/Services/LocalDevelopmentAuthentication.cs)
- [Development identity binding](FT-MONITOR/Services/LocalDevelopmentIdentity.cs)
- [Delegated-token verification](FT-MONITOR/Services/WebDevOpsIdentityVerifier.cs)
- [Browser auth helper](FT-MONITOR/wwwroot/js/msal-auth.js)
- [Chat UI behavior](FT-MONITOR/wwwroot/js/agent-chat.js)
- [Shared owner-aware storage](src/WorkMonitor.Agent/Conversations/SessionStore.cs)
- [Persistent developer credential](src/WorkMonitor.Core/Services/PersistentInteractiveCredential.cs)
- [MCP tool adapter](src/WorkMonitor.Mcp/WorkMonitorMcpTools.cs)
