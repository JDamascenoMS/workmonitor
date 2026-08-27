# 🎯 Implement MSAL.js SPA PKCE Auth for Azure DevOps

## Understanding
Replace the server-side OIDC token acquisition (which is blocked by tenant policy - no client secrets, no admin consent) with a browser-side MSAL.js PKCE flow. The browser acquires the Azure DevOps access token directly, sends it as a Bearer header to the backend API, and the backend reads it from the request header. Server-side Entra ID login stays for page protection.

## Assumptions
- The Entra ID app registration (client ID `7e3ce349-738d-45e3-ae34-9fdae87bea92`) will have a **SPA** platform redirect URI added manually by the user
- The Azure DevOps `user_impersonation` scope (`499b84ac-1321-427f-aa17-267ca6975798/user_impersonation`) is user-consentable and doesn't need admin consent when acquired via a public client (SPA) flow
- MSAL.js can leverage SSO from the existing Entra ID cookie-based login, enabling silent token acquisition most of the time
- The `parameters.html` page doesn't need DevOps tokens (it only calls the parameters API), so it only needs the `/api/me` user-info call
- The `index.html` page only fetches `/api/me` and doesn't need DevOps tokens either

## Approach
1. **Simplify `Program.cs`**: Remove the `ResponseType = "id_token token"`, `SaveTokens`, and Azure DevOps scope from the OIDC config. Remove session storage (no longer needed for tokens). Keep the standard `AddMicrosoftIdentityWebApp` for page protection only.

2. **Create `msal-auth.js`**: A shared JavaScript module that initializes MSAL.js, silently acquires an Azure DevOps token using the SSO session, and provides an `authFetch()` helper that attaches the Bearer header to all API calls.

3. **Update `MonitorController.cs`**: Change `GetAccessTokenAsync()` to read the token from the `Authorization: Bearer <token>` request header instead of from `HttpContext.GetTokenAsync()`.

4. **Update all HTML pages**: Add MSAL.js CDN script and `msal-auth.js`, replace bare `fetch()` calls to API endpoints with `authFetch()`.

## Key Files
- FT-MONITOR/Program.cs - simplify auth config, remove token/session storage
- FT-MONITOR/wwwroot/js/msal-auth.js - new shared MSAL.js helper
- FT-MONITOR/Controllers/MonitorController.cs - read token from request header
- FT-MONITOR/wwwroot/top-bugs.html - update all fetch calls
- FT-MONITOR/wwwroot/top-feedback.html - update all fetch calls
- FT-MONITOR/wwwroot/feedback-dashboard.html - update all fetch calls
- FT-MONITOR/wwwroot/my-dashboard.html - update all fetch calls
- FT-MONITOR/wwwroot/parameters.html - update fetch calls
- FT-MONITOR/wwwroot/index.html - update fetch calls

## Risks & Open Questions
- The user must manually add a **SPA** platform redirect URI in the Entra ID app registration portal
- Silent token acquisition requires a valid Entra ID SSO session; if it expires, MSAL.js will show a popup or redirect
- Pages that don't call DevOps APIs (index, parameters) still need authFetch for their non-DevOps API calls since the controller requires [Authorize]

**Progress**: 100% [██████████]

**Last Updated**: 2026-03-25 23:48:29

## 📝 Plan Steps
- ✅ **Simplify Program.cs to remove DevOps-specific OIDC config and session-based token storage**
- ✅ **Create FT-MONITOR/wwwroot/js/msal-auth.js shared MSAL.js helper with authFetch**
- ✅ **Update MonitorController.cs to read access token from Authorization request header**
- ✅ **Update top-bugs.html to include MSAL.js and use authFetch for all API calls**
- ✅ **Update top-feedback.html to include MSAL.js and use authFetch for all API calls**
- ✅ **Update feedback-dashboard.html to include MSAL.js and use authFetch for all API calls**
- ✅ **Update my-dashboard.html to include MSAL.js and use authFetch for all API calls**
- ✅ **Update parameters.html to include MSAL.js and use authFetch for all API calls**
- ✅ **Update index.html to include MSAL.js and use authFetch for API calls**
- ✅ **Build and verify the solution compiles successfully**

