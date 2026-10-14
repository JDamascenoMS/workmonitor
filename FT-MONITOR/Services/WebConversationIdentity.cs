using System.Security.Claims;
using Microsoft.Identity.Web;

namespace FT_MONITOR.Services;

public static class WebConversationIdentity
{
    public static string GetOwnerId(ClaimsPrincipal user)
    {
        var tenant = user.GetTenantId();
        var objectId = user.GetObjectId();
        if (user.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(tenant, out var tenantId) ||
            !Guid.TryParse(objectId, out var userId))
        {
            throw new WebAuthenticationException(
                StatusCodes.Status403Forbidden,
                "identity_claims_missing",
                "The signed-in session is missing its tenant or object identity. Sign in again.");
        }

        return $"{tenantId:D}:{userId:D}";
    }
}
