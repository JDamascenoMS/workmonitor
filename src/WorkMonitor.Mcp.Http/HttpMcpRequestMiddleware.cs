using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Client;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;

namespace WorkMonitor.Mcp.Http;

public sealed class HttpMcpRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HttpMcpSettings settings,
        IDevOpsOboTokenAcquirer acquirer, RequestDevOpsToken requestToken)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            return Task.CompletedTask;
        });
        if (context.Request.Headers.ContainsKey("Origin"))
        {
            await Error(context, 403, "browser_origin_not_supported",
                "This endpoint accepts server-to-server clients without an Origin header, not browser-origin requests.");
            return;
        }
        if (!context.Request.IsHttps)
        {
            await Error(context, 400, "https_required", "Use an approved HTTPS endpoint.");
            return;
        }
        if (context.Request.Headers.ContainsKey("X-WorkMonitor-DevOps-Token"))
        {
            await Error(context, 400, "token_override_not_supported", "Supply only the WorkMonitor API bearer token. Azure DevOps tokens are acquired on behalf of the validated user.");
            return;
        }
        if (context.Request.Headers.ContainsKey("Mcp-Session-Id"))
        {
            await Error(context, 400, "sessions_not_supported", "This host is stateless. Do not send MCP session identifiers.");
            return;
        }
        if (!string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/mcp", StringComparison.OrdinalIgnoreCase) ||
            !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        // Preflight before the transport starts SSE: OBO interaction must be an HTTP claims challenge.
        context.Request.EnableBuffering(bufferThreshold: 65536, bufferLimit: 65536);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body,
                new JsonDocumentOptions { MaxDepth = 16 }, context.RequestAborted);
        }
        catch (JsonException)
        {
            await Error(context, 400, "invalid_request", "Expected one bounded JSON-RPC request.");
            return;
        }
        catch (IOException)
        {
            await Error(context, 413, "request_too_large", "MCP requests must not exceed 64 KiB.");
            return;
        }
        context.Request.Body.Position = 0;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                await Error(context, 400, "invalid_request", "Expected one JSON-RPC request, not a batch.");
                return;
            }
            if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String &&
                method.GetString() == "tools/call")
            {
                var name = root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("name", out var toolName) && toolName.ValueKind == JsonValueKind.String
                    ? toolName.GetString() : null;
                if (name == null || !HttpReadTools.AllowedNames.Contains(name))
                {
                    await ToolError(context, root, "Tool is not available on the read-only HTTP host.");
                    return;
                }
                if (name == "get_work_item")
                {
                    try
                    {
                        GetWorkItemTool.ParseArguments(parameters.TryGetProperty("arguments", out var arguments)
                            ? arguments : default);
                    }
                    catch (ArgumentException)
                    {
                        await ToolError(context, root, "Use a positive integer id and optional view=summary or view=detail; no other arguments are accepted.");
                        return;
                    }
                    if (!await AcquireAsync(context, settings, acquirer, requestToken, context.RequestAborted))
                        return;
                }
                else if (name == "analyze_feedback_themes")
                {
                    if (!context.Request.HasJsonContentType())
                    {
                        await Error(context, 415, "json_content_type_required", "Use a JSON Content-Type for MCP requests.");
                        return;
                    }
                    var themeRequest = root.Clone();
                    context.RequestServices.GetRequiredService<RequestThemeAnalysis>().Preflight = () =>
                        AnalyzeThemesAsync(context, settings, acquirer, requestToken,
                            themeRequest, themeRequest.GetProperty("params"));
                }
            }
        }
        try
        {
            await next(context);
        }
        catch (RequestThemeAnalysis.PreflightRejectedException) when (context.Response.HasStarted)
        {
            // Preflight already wrote the HTTP failure; do not let the SDK start SSE.
            return;
        }
    }

    private static async Task<bool> AcquireAsync(HttpContext context, HttpMcpSettings settings,
        IDevOpsOboTokenAcquirer acquirer, RequestDevOpsToken requestToken, CancellationToken cancellationToken)
    {
        if (!HttpMcpAuthentication.IsDelegatedUser(context.User, settings))
        {
            await Error(context, 403, "delegated_user_required", "A permitted delegated user is required.");
            return false;
        }
        var assertion = await context.GetTokenAsync("access_token");
        if (string.IsNullOrWhiteSpace(assertion))
        {
            await Error(context, 401, "api_token_required", "A validated WorkMonitor API bearer token is required.");
            return false;
        }
        try
        {
            var token = await acquirer.AcquireAsync(assertion, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(token) || token == assertion)
            {
                await Error(context, 502, "invalid_obo_result", "Azure DevOps OBO did not return a separate downstream token.");
                return false;
            }
            requestToken.Token = token;
            return true;
        }
        catch (MsalUiRequiredException ex)
        {
            var claims = EncodeClaims(ex.Claims);
            context.Response.Headers.WWWAuthenticate =
                $"Bearer authorization_uri=\"https://login.microsoftonline.com/{settings.TenantId}/oauth2/v2.0/authorize\", " +
                $"error=\"{(claims == null ? "invalid_token" : "insufficient_claims")}\", " +
                $"scope=\"{settings.ApiScope}\"" + (claims == null ? "" : $", claims=\"{claims}\"");
            await Error(context, 401, "devops_interaction_required",
                "Sign in again with the required claims, or obtain approved Azure DevOps delegated consent for this API.");
        }
        catch (MsalException)
        {
            await Error(context, 502, "obo_failed",
                "Azure DevOps OBO failed. Verify the confidential-client credential, delegated permissions, consent and tenant policy.");
        }
        catch (HttpRequestException)
        {
            await Error(context, 502, "identity_service_unavailable", "The identity service could not complete on-behalf-of authentication.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await Error(context, 504, "identity_service_timeout", "On-behalf-of authentication timed out.");
        }
        return false;
    }

    private static async Task<bool> AnalyzeThemesAsync(HttpContext context, HttpMcpSettings settings,
        IDevOpsOboTokenAcquirer acquirer, RequestDevOpsToken requestToken, JsonElement root, JsonElement parameters)
    {
        var tool = context.RequestServices.GetRequiredService<AnalyzeFeedbackThemesTool>();
        if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != "2.0" || !root.TryGetProperty("id", out var id) ||
            id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            id.GetRawText().Length > 128 || (id.ValueKind == JsonValueKind.Number && !id.TryGetInt64(out _)) ||
            root.EnumerateObject().Select(property => property.Name).Distinct().Count() != root.EnumerateObject().Count())
        {
            await Error(context, 400, "invalid_theme_request", "Expected one JSON-RPC 2.0 theme request with an id and no duplicate fields.");
            return false;
        }
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : JsonSerializer.SerializeToElement(new { });
        try
        {
            if (arguments.ValueKind == JsonValueKind.Object &&
                arguments.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Null))
                throw new ArgumentException("Explicit null theme arguments are not supported.");
            tool.ParseRequest(arguments);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            await ToolError(context, root, "Use a configured projectArea OR saved-query name/GUID, and maxResults between 1 and 50. Call list_saved_queries/list_project_areas; unknown, duplicate or null arguments are not accepted.");
            return false;
        }
        HttpThemePolicy policy;
        try
        {
            policy = context.RequestServices.GetRequiredService<HttpThemePolicy>();
            context.RequestServices.GetRequiredService<HttpThemeModel>().ValidateConfiguration();
        }
        catch (InvalidOperationException)
        {
            await Error(context, 503, "theme_configuration_required",
                "Theme analysis requires valid server AzureOpenAI Endpoint, DeploymentName, explicit Authentication=ManagedIdentity or ApiKey and bounded Themes policy settings. Ask the host administrator; desktop credentials are not used.");
            return false;
        }
        using var lease = policy.Acquire(context.User.FindFirst("tid")!.Value,
            Guid.Parse(context.User.FindFirst("oid")!.Value).ToString("D"));
        if (!lease.IsAcquired)
        {
            var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? Math.Ceiling(retry.TotalSeconds) : 1;
            context.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Error(context, 429, "theme_rate_limit", "Theme analysis is limited per authenticated user. Honor Retry-After and wait for any active analysis to finish.");
            return false;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(policy.Deadline);
        try
        {
            if (!await AcquireAsync(context, settings, acquirer, requestToken, deadline.Token))
                return false;
            var result = await tool.ExecuteAsync(arguments, new AgentContext
            {
                UserName = context.User.FindFirst("oid")!.Value,
                DevOpsTokenProvider = requestToken.GetAsync,
                DryRun = true
            }, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            // Only this HTTP request can consume the preflight result; SDK dispatch never repeats paid work.
            context.RequestServices.GetRequiredService<RequestThemeAnalysis>().Result = result;
            return true;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await Error(context, 504, "theme_analysis_timeout",
                "Theme analysis exceeded its end-to-end deadline or a downstream timeout. No successful result is available; reduce maxResults and retry later.");
        }
        catch (HttpRequestException)
        {
            await Error(context, 502, "theme_source_unavailable",
                "Azure DevOps could not supply authorized feedback. Verify this user's saved-query and work-item access and service availability.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Newtonsoft.Json.JsonException or JsonException)
        {
            await Error(context, 502, "theme_analysis_failed",
                "Theme analysis could not return valid grounded data. Verify the source, server model deployment/identity/key, quota and service availability; no partial success is returned.");
        }
        return false;
    }

    private static string? EncodeClaims(string? claims)
    {
        if (string.IsNullOrWhiteSpace(claims) || claims.Length > 6000)
            return null;
        try
        {
            using var document = JsonDocument.Parse(claims, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("access_token", out var tokenClaims) ||
                tokenClaims.ValueKind != JsonValueKind.Object)
                return null;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document.RootElement)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task Error(HttpContext context, int status, string error, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error, message }, context.RequestAborted);
    }

    private static Task ToolError(HttpContext context, JsonElement root, string message)
    {
        context.Response.StatusCode = 200;
        return context.Response.WriteAsJsonAsync(new
        {
            jsonrpc = "2.0",
            id = root.TryGetProperty("id", out var id) ? (object)id : null,
            error = new { code = -32602, message }
        }, context.RequestAborted);
    }
}
