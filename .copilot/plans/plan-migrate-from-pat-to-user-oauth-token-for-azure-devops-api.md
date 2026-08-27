# 🎯 Migrate from PAT to User OAuth Token for Azure DevOps API

## Understanding
Replace the expired PAT-based authentication in `MonitorClient` with the logged-in user's Entra ID OAuth token. The app already authenticates users via OpenID Connect with Microsoft Entra ID — we need to configure it to also acquire tokens for the Azure DevOps API scope (`499b84ac-1321-427f-aa17-267ca6975798/.default`) using the on-behalf-of flow, then pass the token through the call chain.

## Assumptions
- The Entra ID app registration (ClientId `7e3ce349-738d-45e3-ae34-9fdae87bea92`) can be configured with a client secret to support OBO/auth code token acquisition
- The logged-in user has access to the `devdiv` Azure DevOps organization
- The `VSDivComments` endpoint (`sendvsfeedback2.azurewebsites.net`) does NOT require the user token — it's a public-ish API that never used the PAT

## Approach
Use `Microsoft.Identity.Web`'s `EnableTokenAcquisitionToCallDownstreamApi()` with an in-memory token cache. The Azure DevOps REST API scope is `499b84ac-1321-427f-aa17-267ca6975798/.default`. 

`MonitorClient` will accept a `string accessToken` parameter instead of using the static PAT. `MonitorService` will also accept and forward the token. Controllers will inject `ITokenAcquisition`, acquire the token, and pass it down.

A `ClientSecret` must be added to `appsettings.json` under `AzureAd` (the user will need to create one in the Entra ID portal).

## Key Files
- `FT-MONITOR/appsettings.json` - Add ClientSecret and scope config
- `FT-MONITOR/Program.cs` - Configure downstream API token acquisition
- `FT-MONITOR/Clients/MonitorClient.cs` - Accept token param, use Bearer auth instead of PAT Basic auth
- `FT-MONITOR/Services/MonitorService.cs` - Accept and forward token
- `FT-MONITOR/Controllers/MonitorController.cs` - Inject ITokenAcquisition, acquire token, pass to service/client
- `FT-MONITOR/Controllers/ParametersController.cs` - No change needed (doesn't call DevOps)
- `FT-MONITOR/Controllers/ScaffoldController.cs` - No change needed (doesn't call DevOps)

## Risks & Open Questions
- User must create a client secret in the Entra ID app registration and add it to config
- User must add API permission `Azure DevOps > user_impersonation` in the app registration
- Token cache is in-memory — tokens lost on app restart (acceptable for this app)

**Progress**: 100% [██████████]

**Last Updated**: 2026-03-25 20:25:34

## 📝 Plan Steps
- ✅ **Update appsettings.json to add ClientSecret placeholder and downstream API scope**
- ✅ **Update Program.cs to configure token acquisition for downstream Azure DevOps API**
- ✅ **Modify MonitorClient constructor to accept an access token and use Bearer auth instead of PAT Basic auth**
- ✅ **Modify MonitorService constructor to accept and forward the access token to MonitorClient**
- ✅ **Update MonitorController to inject ITokenAcquisition, acquire token per request, and pass through MonitorService/MonitorClient**
- ✅ **Build and verify compilation**

