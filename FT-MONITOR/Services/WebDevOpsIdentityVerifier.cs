using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CoreConstants = WorkMonitor.Core.Helpers.Constants;

namespace FT_MONITOR.Services;

public sealed record VerifiedDevOpsIdentity(Guid TenantId, Guid ObjectId)
{
    public string OwnerId => $"{TenantId:D}:{ObjectId:D}";
}

public sealed class WebDevOpsIdentityVerifier(HttpClient httpClient, ILogger logger)
{
    public async Task<VerifiedDevOpsIdentity> VerifyAsync(
        string token, CancellationToken cancellationToken)
    {
        var organization = Uri.EscapeDataString(CoreConstants.Organization);
        using var connection = await GetIdentityDocumentAsync(
            $"https://dev.azure.com/{organization}/_apis/connectionData?api-version=7.1-preview.1",
            token, cancellationToken);
        if (connection.RootElement.ValueKind != JsonValueKind.Object ||
            !connection.RootElement.TryGetProperty("authenticatedUser", out var user) ||
            user.ValueKind != JsonValueKind.Object ||
            !user.TryGetProperty("subjectDescriptor", out var descriptor) ||
            descriptor.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(descriptor.GetString()))
            throw IdentityVerificationFailed();

        using var graphUser = await GetIdentityDocumentAsync(
            $"https://vssps.dev.azure.com/{organization}/_apis/graph/users/" +
            $"{Uri.EscapeDataString(descriptor.GetString()!)}?api-version=7.1-preview.1",
            token, cancellationToken);
        var root = graphUser.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("origin", out var origin) ||
            origin.ValueKind != JsonValueKind.String ||
            !string.Equals(origin.GetString(), "aad", StringComparison.OrdinalIgnoreCase) ||
            !TryGetGuid(root, "domain", out var tenantId) ||
            !TryGetGuid(root, "originId", out var objectId))
            throw IdentityVerificationFailed();
        return new VerifiedDevOpsIdentity(tenantId, objectId);
    }

    private async Task<JsonDocument> GetIdentityDocumentAsync(
        string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Unable to reach Azure DevOps for identity verification.");
            throw IdentityServiceUnavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Azure DevOps identity verification timed out.");
            throw IdentityServiceUnavailable();
        }
        using var ownedResponse = response;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new WebAuthenticationException(
                StatusCodes.Status403Forbidden, "devops_access_denied",
                "Azure DevOps rejected this account's access. Sign in again and check organization membership and consent.");
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Azure DevOps identity verification returned HTTP {StatusCode}.",
                (int)response.StatusCode);
            throw IdentityServiceUnavailable();
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            logger.LogWarning("Azure DevOps identity verification returned invalid JSON.");
            throw IdentityVerificationFailed();
        }
    }

    private static bool TryGetGuid(JsonElement element, string property, out Guid value)
    {
        value = default;
        return element.TryGetProperty(property, out var field) &&
            field.ValueKind == JsonValueKind.String &&
            Guid.TryParse(field.GetString(), out value) && value != Guid.Empty;
    }

    private static WebAuthenticationException IdentityVerificationFailed()
        => new(StatusCodes.Status403Forbidden, "devops_identity_unverified",
            "Azure DevOps could not verify this account's Entra identity. Check organization membership.");

    private static WebAuthenticationException IdentityServiceUnavailable()
        => new(StatusCodes.Status502BadGateway, "devops_identity_unavailable",
            "Azure DevOps identity verification failed. Try again or check organization configuration.");
}
