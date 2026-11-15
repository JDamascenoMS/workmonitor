using Microsoft.Identity.Client;

namespace WorkMonitor.Mcp.Http;

public interface IDevOpsOboTokenAcquirer
{
    Task<string> AcquireAsync(string validatedUserAssertion, CancellationToken cancellationToken);
}

public sealed class DevOpsOboTokenAcquirer(HttpMcpSettings settings, IHttpClientFactory clients)
    : IDevOpsOboTokenAcquirer
{
    public const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    public async Task<string> AcquireAsync(string validatedUserAssertion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = ConfidentialClientApplicationBuilder.Create(settings.ClientId)
            .WithAuthority(settings.Authority)
            .WithHttpClientFactory(new MsalHttpClientFactory(clients))
            .WithClientCapabilities(["cp1"]);
        if (settings.Certificate != null)
            builder.WithCertificate(settings.Certificate);
        else
            builder.WithClientSecret(settings.ClientSecret);

        // A fresh confidential client has no shared user token cache or desktop credential fallback.
        var result = await builder.Build().AcquireTokenOnBehalfOf(
            [Scope], new UserAssertion(validatedUserAssertion)).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    private sealed class MsalHttpClientFactory(IHttpClientFactory clients) : IMsalHttpClientFactory
    {
        public HttpClient GetHttpClient() => clients.CreateClient("EntraObo");
    }
}

public sealed class RequestDevOpsToken
{
    internal string? Token { get; set; }

    public Task<string> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Token ?? throw new InvalidOperationException(
            "Request-bound Azure DevOps on-behalf-of authentication is required."));
    }
}
