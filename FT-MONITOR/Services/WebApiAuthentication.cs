using System.Security.Claims;

namespace FT_MONITOR.Services;

public static class WebApiAuthentication
{
    public const string RequiredScope = "access_as_user";

    public static bool HasDelegatedScope(ClaimsPrincipal user)
        => user.FindAll("scp")
            .Concat(user.FindAll("http://schemas.microsoft.com/identity/claims/scope"))
            .Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(RequiredScope, StringComparer.Ordinal));

    public static BrowserAuthenticationConfiguration GetBrowserConfiguration(
        IConfiguration configuration)
    {
        var localDevelopment = WebAuthenticationModeConfiguration.GetMode(configuration) ==
            WebAuthenticationMode.LocalDevelopment;
        var configured =
            Guid.TryParse(configuration["AzureAd:TenantId"], out var tenantId) &&
            tenantId != Guid.Empty;
        configured &= Guid.TryParse(configuration["AzureAd:ClientId"], out var clientId) &&
            clientId != Guid.Empty;
        var instance = configuration["AzureAd:Instance"] ??
            "https://login.microsoftonline.com/";
        configured &= Uri.TryCreate(instance, UriKind.Absolute, out var instanceUri) &&
            instanceUri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(instanceUri.UserInfo) &&
            string.IsNullOrEmpty(instanceUri.Query) &&
            string.IsNullOrEmpty(instanceUri.Fragment) &&
            instanceUri.AbsolutePath == "/";

        return new BrowserAuthenticationConfiguration(
            configured,
            configured ? null : "Configure AzureAd:TenantId, ClientId, and a valid HTTPS Instance.",
            localDevelopment ? "localDevelopment" : "spaPkce",
            configured ? clientId.ToString("D") : null,
            configured ? $"{instance.TrimEnd('/')}/{tenantId:D}" : null,
            !localDevelopment && configured ? $"api://{clientId:D}/{RequiredScope}" : null,
            localDevelopment ? null : WebDevOpsTokenService.DelegatedScope,
            localDevelopment ? "/signin-oidc" : "/auth-callback.html",
            localDevelopment ? "/MicrosoftIdentity/Account/SignIn" : null,
            localDevelopment ? "/api/agent/csrf" : null,
            localDevelopment ? "/api/auth/local-sign-out" : null,
            localDevelopment
                ? "Development mode: localhost only; read-only DevOps uses the developer's saved Azure identity. Your sign-in must match that identity."
                : null);
    }
}

public sealed record BrowserAuthenticationConfiguration(
    bool Configured,
    string? Error,
    string Mode,
    string? ClientId,
    string? Authority,
    string? ApiScope,
    string? DevOpsScope,
    string RedirectPath,
    string? SignInPath = null,
    string? CsrfPath = null,
    string? SignOutPath = null,
    string? Warning = null);
