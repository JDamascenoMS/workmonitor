namespace FT_MONITOR.Services;

public interface IWebDevOpsTokenService
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class WebDevOpsTokenService(
    HttpClient httpClient,
    IHttpContextAccessor httpContextAccessor,
    ILogger<WebDevOpsTokenService> logger) : IWebDevOpsTokenService
{
    public const string DelegatedScope =
        "499b84ac-1321-427f-aa17-267ca6975798/user_impersonation";
    public const string TokenHeader = "X-WorkMonitor-DevOps-Token";
    private string? _validatedToken;
    private string? _validatedOwner;
    private HttpContext? _validatedContext;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = httpContextAccessor.HttpContext;
        if (context?.User.Identity?.IsAuthenticated != true)
            throw new WebAuthenticationException(
                StatusCodes.Status401Unauthorized, "sign_in_required",
                "Sign in before accessing Azure DevOps.");

        var owner = WebConversationIdentity.GetOwnerId(context.User);
        var values = context.Request.Headers[TokenHeader];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
            throw new WebAuthenticationException(
                StatusCodes.Status403Forbidden, "devops_token_required",
                "Sign in and allow Azure DevOps access for the selected browser account.");
        var token = values[0]!;
        if (token.Length > 16384 || token.Any(char.IsWhiteSpace))
            throw new WebAuthenticationException(
                StatusCodes.Status400BadRequest, "invalid_devops_token",
                "The Azure DevOps token header is invalid. Sign in again.");
        if (_validatedToken == token && _validatedOwner == owner &&
            ReferenceEquals(_validatedContext, context))
            return token;

        var identity = await new WebDevOpsIdentityVerifier(httpClient, logger)
            .VerifyAsync(token, cancellationToken);
        if (identity.OwnerId != owner)
            throw new WebAuthenticationException(
                StatusCodes.Status403Forbidden, "devops_identity_mismatch",
                "Azure DevOps access belongs to another account. Sign out and sign in with one account.");
        _validatedToken = token;
        _validatedOwner = owner;
        _validatedContext = context;
        return token;
    }
}

public sealed class WebAuthenticationException(
    int statusCode,
    string errorCode,
    string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorCode { get; } = errorCode;
}
