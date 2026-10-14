using System.Security.Claims;
using Azure.Identity;
using WorkMonitor.Core.Services;

namespace FT_MONITOR.Services;

public interface ILocalDevelopmentIdentity
{
    Task<VerifiedDevOpsIdentity> GetIdentityAsync(CancellationToken cancellationToken);
    Task<string> GetTokenForUserAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}

public sealed class LocalDevelopmentIdentity(
    DevOpsTokenService tokens,
    WebDevOpsIdentityVerifier verifier,
    ILogger<LocalDevelopmentIdentity> logger) : ILocalDevelopmentIdentity
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _verifiedToken;
    private VerifiedDevOpsIdentity? _identity;

    public async Task<VerifiedDevOpsIdentity> GetIdentityAsync(CancellationToken cancellationToken)
    {
        var token = await GetDeveloperTokenAsync(cancellationToken);
        return await VerifyIdentityAsync(token, cancellationToken);
    }

    public async Task<string> GetTokenForUserAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (user.Identity?.IsAuthenticated != true)
            throw new WebAuthenticationException(401, "sign_in_required",
                "Sign in before accessing the local developer's work.");
        var owner = WebConversationIdentity.GetOwnerId(user);
        var token = await GetDeveloperTokenAsync(cancellationToken);
        var identity = await VerifyIdentityAsync(token, cancellationToken);
        if (identity.OwnerId != owner)
            throw new WebAuthenticationException(
                StatusCodes.Status403Forbidden, "local_developer_mismatch",
                "This localhost instance only permits the developer's saved Azure account.");
        return token;
    }

    private async Task<string> GetDeveloperTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await tokens.GetTokenAsync(cancellationToken);
        }
        catch (AuthenticationRequiredException)
        {
            throw DeveloperSignInRequired();
        }
        catch (CredentialUnavailableException)
        {
            throw DeveloperSignInRequired();
        }
        catch (AuthenticationFailedException)
        {
            logger.LogWarning("The local developer's saved Azure authentication failed.");
            throw DeveloperSignInRequired();
        }
    }

    private async Task<VerifiedDevOpsIdentity> VerifyIdentityAsync(
        string token, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_verifiedToken == token && _identity is not null)
                return _identity;
            var identity = await verifier.VerifyAsync(token, cancellationToken);
            _verifiedToken = token;
            _identity = identity;
            return identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static WebAuthenticationException DeveloperSignInRequired()
        => new(StatusCodes.Status503ServiceUnavailable, "local_developer_login_required",
            "The developer's Azure cache needs sign-in. Run workmonitor --auth-login and retry. No browser or host-identity fallback is used.");
}

public sealed class LocalDevelopmentDevOpsTokenService(
    ILocalDevelopmentIdentity identity,
    IHttpContextAccessor context) : IWebDevOpsTokenService
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        => identity.GetTokenForUserAsync(
            context.HttpContext?.User ?? new ClaimsPrincipal(), cancellationToken);
}
