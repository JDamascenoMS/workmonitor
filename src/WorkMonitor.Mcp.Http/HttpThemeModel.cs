using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using WorkMonitor.Agent;

namespace WorkMonitor.Mcp.Http;

public sealed class HttpThemeModel : IFeedbackThemeClassifier
{
    private readonly Lazy<TokenCredential?> _credential;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _clients;

    public HttpThemeModel(IConfiguration configuration, IHttpClientFactory clients)
    {
        _configuration = configuration;
        _clients = clients;
        _credential = new Lazy<TokenCredential?>(() =>
        {
            var section = configuration.GetSection("AzureOpenAI");
            if (!Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != "https" || !string.IsNullOrEmpty(endpoint.UserInfo) ||
                endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
                string.IsNullOrWhiteSpace(section["DeploymentName"]) || section["DeploymentName"]!.Length > 128 ||
                section["DeploymentName"]!.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
                throw ConfigurationError();
            TokenCredential? credential = null;
            if (section["Authentication"] == "ManagedIdentity" && string.IsNullOrWhiteSpace(section["ApiKey"]))
            {
                var clientId = section["ManagedIdentityClientId"];
                if (!string.IsNullOrEmpty(clientId) && (!Guid.TryParse(clientId, out var id) || id == Guid.Empty))
                    throw ConfigurationError();
                credential = new ManagedIdentityCredential(clientId, new TokenCredentialOptions
                {
                    Transport = new HttpClientTransport(clients.CreateClient("ModelIdentity")),
                    Retry = { MaxRetries = 0 },
                    Diagnostics = { IsLoggingEnabled = false, IsLoggingContentEnabled = false }
                });
            }
            else if (section["Authentication"] != "ApiKey" || string.IsNullOrWhiteSpace(section["ApiKey"]) ||
                !string.IsNullOrEmpty(section["ManagedIdentityClientId"]))
                throw ConfigurationError();
            return credential;
        });
    }

    public void ValidateConfiguration() => _ = _credential.Value;

    public async Task<string> ClassifyFeedbackThemes(string feedbackJson, CancellationToken cancellationToken = default)
    {
        try
        {
            var credential = _credential.Value;
            using var client = _clients.CreateClient("ThemeModel");
            var model = new AzureOpenAIService(_configuration,
                credential == null ? null : new DeadlineModelCredential(credential, cancellationToken),
                new AzureOpenAIClientOptions
                {
                    Transport = new HttpClientPipelineTransport(client),
                    RetryPolicy = new ClientRetryPolicy(0)
                });
            return await model.ClassifyFeedbackThemes(feedbackJson, cancellationToken);
        }
        catch (ClientResultException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Azure OpenAI theme execution failed. Verify the server deployment, identity/key, resource role, quota and service availability.");
        }
        catch (AuthenticationFailedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The server model identity could not authenticate. Verify its approved managed identity and Cognitive Services OpenAI User role.");
        }
        catch (HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The server could not reach Azure OpenAI. Verify approved network access and service availability.");
        }
    }

    // Azure OpenAI 2.1.0's authentication policy passes default instead of the model call's cancellation token.
    internal sealed class DeadlineModelCredential(TokenCredential inner, CancellationToken deadline) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            return inner.GetToken(requestContext, linked.Token);
        }

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            return await inner.GetTokenAsync(requestContext, linked.Token);
        }
    }

    private static InvalidOperationException ConfigurationError() => new(
        "Configure AzureOpenAI Endpoint (HTTPS origin), DeploymentName and Authentication=ManagedIdentity or ApiKey. " +
        "Use an approved server managed identity (optional ManagedIdentityClientId) or explicitly injected ApiKey, never both. Desktop credentials are not used.");
}

public sealed class BoundedHttpResponseHandler(int maxBytes) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidOperationException("The downstream response exceeded the host byte limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new InvalidOperationException("The downstream response exceeded the host byte limit.");
                buffer.Write(bytes, 0, read);
            }
            var content = new ByteArrayContent(buffer.ToArray());
            foreach (var header in response.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = content;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
