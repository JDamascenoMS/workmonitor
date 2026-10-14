using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FT_MONITOR.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using WorkMonitor.Core.Helpers;

namespace WorkMonitor.Tests;

public sealed class WebAuthenticationTests
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string Alice = "11111111-1111-1111-1111-111111111111";
    private const string Bob = "22222222-2222-2222-2222-222222222222";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";
    private const string OpaqueToken = "opaque-downstream-resource-token";
    private const string Descriptor = "aad.subject/with+reserved?characters=#";

    [Fact]
    public async Task OpaqueTokenIsVerifiedAtFixedTrustedEndpointsWithoutClientCredentials()
    {
        using var fixture = new TokenFixture();
        fixture.HttpClient.BaseAddress = new Uri("https://untrusted.example/");
        fixture.HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "host-identity");

        var token = await fixture.Service.GetTokenAsync();

        Assert.True(token == OpaqueToken, "The verified browser token should be returned unchanged.");
        Assert.Equal(2, fixture.Handler.Requests.Count);
        var organization = Uri.EscapeDataString(Constants.Organization);
        Assert.Equal(
            $"https://dev.azure.com/{organization}/_apis/connectionData?api-version=7.1-preview.1",
            fixture.Handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(
            $"https://vssps.dev.azure.com/{organization}/_apis/graph/users/" +
            $"{Uri.EscapeDataString(Descriptor)}?api-version=7.1-preview.1",
            fixture.Handler.Requests[1].Uri.AbsoluteUri);
        Assert.All(fixture.Handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Scheme);
            Assert.True(request.Token == OpaqueToken, "Only the browser resource token may be forwarded.");
        });
    }

    [Fact]
    public async Task ValidatedTokenIsReusedOnlyWithinSameRequestAndOwner()
    {
        using var fixture = new TokenFixture();

        await fixture.Service.GetTokenAsync();
        await fixture.Service.GetTokenAsync();

        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task CachedTokenCannotBeReusedByAnotherApiOwner()
    {
        using var fixture = new TokenFixture();
        await fixture.Service.GetTokenAsync();
        fixture.Context.User = UserWithIdentity(objectId: Bob);

        await AssertAuthenticationErrorAsync(
            fixture.Service, 403, "devops_identity_mismatch");

        Assert.Equal(4, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task DifferentTokenMustBeVerifiedAgainForSameOwner()
    {
        using var fixture = new TokenFixture();
        await fixture.Service.GetTokenAsync();
        fixture.Context.Request.Headers[WebDevOpsTokenService.TokenHeader] =
            "another-opaque-resource-token";

        await fixture.Service.GetTokenAsync();

        Assert.Equal(4, fixture.Handler.Requests.Count);
        Assert.All(fixture.Handler.Requests.Skip(2), request =>
            Assert.True(request.Token == "another-opaque-resource-token",
                "The new browser token must be verified instead of reusing the old token."));
    }

    [Fact]
    public async Task ValidatedTokenCacheIsNotSharedAcrossRequestServiceInstances()
    {
        using var firstRequest = new TokenFixture();
        await firstRequest.Service.GetTokenAsync();
        using var secondRequest = new TokenFixture();

        await secondRequest.Service.GetTokenAsync();

        Assert.Equal(2, firstRequest.Handler.Requests.Count);
        Assert.Equal(2, secondRequest.Handler.Requests.Count);
    }

    [Fact]
    public async Task CachedTokenCannotBeReusedAcrossHttpContexts()
    {
        using var fixture = new TokenFixture();
        await fixture.Service.GetTokenAsync();
        fixture.Accessor.HttpContext = ContextWithIdentity();

        await fixture.Service.GetTokenAsync();

        Assert.Equal(4, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("44444444-4444-4444-4444-444444444444", Alice)]
    [InlineData(Tenant, Bob)]
    public async Task GraphIdentityMustMatchBothVerifiedTenantAndObject(string domain, string originId)
    {
        using var fixture = new TokenFixture();
        fixture.Handler.GraphJson = GraphIdentity(domain, originId);

        await AssertAuthenticationErrorAsync(
            fixture.Service, 403, "devops_identity_mismatch");

        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"authenticatedUser\":null}")]
    [InlineData("{\"authenticatedUser\":[]}")]
    [InlineData("{\"authenticatedUser\":{}}")]
    [InlineData("{\"authenticatedUser\":{\"subjectDescriptor\":42}}")]
    [InlineData("{\"authenticatedUser\":{\"subjectDescriptor\":\"\"}}")]
    [InlineData("{\"authenticatedUser\":{\"subjectDescriptor\":\" \"}}")]
    [InlineData("not-json")]
    public async Task UnidentifiableConnectionIdentityIsRejected(string document)
    {
        using var fixture = new TokenFixture();
        fixture.Handler.ConnectionJson = document;

        await AssertAuthenticationErrorAsync(
            fixture.Service, 403, "devops_identity_unverified");

        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"scalar\"")]
    [InlineData("not-json")]
    [InlineData("{\"origin\":\"msa\",\"domain\":\"" + Tenant + "\",\"originId\":\"" + Alice + "\"}")]
    [InlineData("{\"origin\":42,\"domain\":\"" + Tenant + "\",\"originId\":\"" + Alice + "\"}")]
    [InlineData("{\"origin\":\"aad\",\"originId\":\"" + Alice + "\"}")]
    [InlineData("{\"origin\":\"aad\",\"domain\":\"" + Tenant + "\"}")]
    [InlineData("{\"origin\":\"aad\",\"domain\":\"not-a-guid\",\"originId\":\"" + Alice + "\"}")]
    [InlineData("{\"origin\":\"aad\",\"domain\":\"" + Tenant + "\",\"originId\":\"not-a-guid\"}")]
    [InlineData("{\"origin\":\"aad\",\"domain\":42,\"originId\":\"" + Alice + "\"}")]
    [InlineData("{\"origin\":\"aad\",\"domain\":\"" + Tenant + "\",\"originId\":42}")]
    [InlineData("{\"origin\":\"aad\",\"domain\":\"00000000-0000-0000-0000-000000000000\",\"originId\":\"" + Alice + "\"}")]
    [InlineData("{\"origin\":\"aad\",\"domain\":\"" + Tenant + "\",\"originId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task UnidentifiableGraphIdentityIsRejected(string document)
    {
        using var fixture = new TokenFixture();
        fixture.Handler.GraphJson = document;

        await AssertAuthenticationErrorAsync(
            fixture.Service, 403, "devops_identity_unverified");

        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(1, 401, 403, "devops_access_denied")]
    [InlineData(1, 403, 403, "devops_access_denied")]
    [InlineData(2, 401, 403, "devops_access_denied")]
    [InlineData(2, 403, 403, "devops_access_denied")]
    [InlineData(1, 302, 502, "devops_identity_unavailable")]
    [InlineData(2, 307, 502, "devops_identity_unavailable")]
    [InlineData(1, 429, 502, "devops_identity_unavailable")]
    [InlineData(2, 500, 502, "devops_identity_unavailable")]
    public async Task DownstreamFailuresDoNotAuthenticateOrFollowRedirects(
        int failingCall, int downstreamStatus, int expectedStatus, string errorCode)
    {
        using var fixture = new TokenFixture();
        fixture.Handler.OnSend = (_, call, _) => call == failingCall
            ? Task.FromResult(new HttpResponseMessage((HttpStatusCode)downstreamStatus)
            {
                Headers = { Location = new Uri("https://untrusted.example/redirect") }
            })
            : Task.FromResult(fixture.Handler.IdentityResponse(call));

        await AssertAuthenticationErrorAsync(fixture.Service, expectedStatus, errorCode);

        Assert.Equal(failingCall, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task RejectedTokenIsNotCached()
    {
        using var fixture = new TokenFixture();
        fixture.Handler.GraphJson = GraphIdentity(Tenant, Bob);
        await AssertAuthenticationErrorAsync(
            fixture.Service, 403, "devops_identity_mismatch");
        fixture.Handler.GraphJson = GraphIdentity(Tenant, Alice);

        await fixture.Service.GetTokenAsync();

        Assert.Equal(4, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task TransportFailureOrTimeoutIsAnActionableServiceError(int failingCall, bool timeout)
    {
        using var fixture = new TokenFixture();
        fixture.Handler.OnSend = (_, call, _) =>
        {
            if (call != failingCall)
                return Task.FromResult(fixture.Handler.IdentityResponse(call));
            return Task.FromException<HttpResponseMessage>(timeout
                ? new TaskCanceledException("Downstream timeout")
                : new HttpRequestException("Downstream transport failure"));
        };

        await AssertAuthenticationErrorAsync(fixture.Service, 502, "devops_identity_unavailable");

        Assert.Equal(failingCall, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyTokenHeaderDoesNotFallBackToHostIdentity(bool emptyHeader)
    {
        using var fixture = new TokenFixture();
        fixture.Context.Request.Headers.Remove(WebDevOpsTokenService.TokenHeader);
        if (emptyHeader)
            fixture.Context.Request.Headers[WebDevOpsTokenService.TokenHeader] = "";

        await AssertAuthenticationErrorAsync(fixture.Service, 403, "devops_token_required");

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task MultipleTokenHeadersAreRejectedWithoutNetwork()
    {
        using var fixture = new TokenFixture();
        fixture.Context.Request.Headers[WebDevOpsTokenService.TokenHeader] =
            new StringValues(new[] { OpaqueToken, "another-token" });

        await AssertAuthenticationErrorAsync(fixture.Service, 403, "devops_token_required");

        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("opaque token")]
    [InlineData("opaque\ttoken")]
    [InlineData("opaque\r\ntoken")]
    public async Task WhitespaceTokenHeaderIsRejectedWithoutNetwork(string token)
    {
        using var fixture = new TokenFixture();
        fixture.Context.Request.Headers[WebDevOpsTokenService.TokenHeader] = token;

        await AssertAuthenticationErrorAsync(fixture.Service, 400, "invalid_devops_token");

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task OversizedTokenHeaderIsRejectedWithoutNetwork()
    {
        using var fixture = new TokenFixture();
        fixture.Context.Request.Headers[WebDevOpsTokenService.TokenHeader] = new string('a', 16385);

        await AssertAuthenticationErrorAsync(fixture.Service, 400, "invalid_devops_token");

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task MaximumSizedTokenHeaderIsAccepted()
    {
        using var fixture = new TokenFixture();
        fixture.Context.Request.Headers[WebDevOpsTokenService.TokenHeader] = new string('a', 16384);

        await fixture.Service.GetTokenAsync();

        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousOrMissingHttpContextIsRejectedWithoutNetwork(bool missingContext)
    {
        using var fixture = new TokenFixture();
        if (missingContext)
            fixture.Accessor.HttpContext = null;
        else
            fixture.Context.User = new ClaimsPrincipal();

        await AssertAuthenticationErrorAsync(fixture.Service, 401, "sign_in_required");

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task MissingVerifiedOwnerClaimsIsRejectedWithoutNetwork()
    {
        using var fixture = new TokenFixture();
        fixture.Context.User = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim("name", "Alice") }, "Test"));

        await AssertAuthenticationErrorAsync(fixture.Service, 403, "identity_claims_missing");

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task PreCancelledRequestMakesNoNetworkCall()
    {
        using var fixture = new TokenFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.GetTokenAsync(cancellation.Token));

        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationIsForwardedToBothIdentityCalls(int cancelledCall)
    {
        using var fixture = new TokenFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.OnSend = async (_, call, token) =>
        {
            if (call == cancelledCall)
            {
                Assert.True(token.CanBeCanceled);
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
            }
            return fixture.Handler.IdentityResponse(call);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.GetTokenAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(cancelledCall, fixture.Handler.Requests.Count);
        Assert.True(fixture.Handler.Requests[^1].CancellationToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("scp", "access_as_user", true)]
    [InlineData("scp", "other access_as_user another", true)]
    [InlineData("http://schemas.microsoft.com/identity/claims/scope", "access_as_user", true)]
    [InlineData("scp", "user_impersonation", false)]
    [InlineData("scp", "access_as_user_extra", false)]
    [InlineData("scp", "ACCESS_AS_USER", false)]
    [InlineData("roles", "access_as_user", false)]
    [InlineData("scp", "", false)]
    public void OnlyExactDelegatedApiScopeSatisfiesAuthorization(
        string claimType, string scopes, bool expected)
    {
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(claimType, scopes) }, "Test"));

        Assert.Equal(expected, WebApiAuthentication.HasDelegatedScope(user));
    }

    [Fact]
    public void BrowserReadinessRequiresNoSecretOrCertificate()
    {
        var configuration = Configuration(Tenant, ClientId, "https://login.microsoftonline.com/");

        var result = WebApiAuthentication.GetBrowserConfiguration(configuration);

        Assert.True(result.Configured);
        Assert.Null(result.Error);
        Assert.Equal("spaPkce", result.Mode);
        Assert.Equal(ClientId, result.ClientId);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}", result.Authority);
        Assert.Equal($"api://{ClientId}/access_as_user", result.ApiScope);
        Assert.Equal(WebDevOpsTokenService.DelegatedScope, result.DevOpsScope);
        Assert.Equal("/auth-callback.html", result.RedirectPath);
    }

    [Theory]
    [InlineData(null, ClientId, "https://login.microsoftonline.com/")]
    [InlineData("invalid", ClientId, "https://login.microsoftonline.com/")]
    [InlineData("00000000-0000-0000-0000-000000000000", ClientId, "https://login.microsoftonline.com/")]
    [InlineData(Tenant, null, "https://login.microsoftonline.com/")]
    [InlineData(Tenant, "invalid", "https://login.microsoftonline.com/")]
    [InlineData(Tenant, "00000000-0000-0000-0000-000000000000", "https://login.microsoftonline.com/")]
    [InlineData(Tenant, ClientId, "http://login.microsoftonline.com/")]
    [InlineData(Tenant, ClientId, "not-a-url")]
    [InlineData(Tenant, ClientId, "https://username@login.microsoftonline.com/")]
    [InlineData(Tenant, ClientId, "https://login.microsoftonline.com/?query=value")]
    [InlineData(Tenant, ClientId, "https://login.microsoftonline.com/#fragment")]
    [InlineData(Tenant, ClientId, "https://login.microsoftonline.com/extra-path/")]
    public void InvalidPublicConfigurationIsNotReady(
        string? tenant, string? client, string instance)
    {
        var result = WebApiAuthentication.GetBrowserConfiguration(
            Configuration(tenant, client, instance));

        Assert.False(result.Configured);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Null(result.ClientId);
        Assert.Null(result.Authority);
        Assert.Null(result.ApiScope);
    }

    [Fact]
    public void DefaultEntraInstanceIsUsableWithoutCredentials()
    {
        var result = WebApiAuthentication.GetBrowserConfiguration(
            Configuration(Tenant, ClientId, null));

        Assert.True(result.Configured);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}", result.Authority);
    }

    [Fact]
    public void ConversationOwnerRequiresStableTenantAndObjectClaims()
    {
        var owner = WebConversationIdentity.GetOwnerId(UserWithIdentity());
        Assert.Equal($"{Tenant}:{Alice}", owner);

        var nameOnly = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim("name", "same-display-name") }, "Test"));
        Assert.Throws<WebAuthenticationException>(() =>
            WebConversationIdentity.GetOwnerId(nameOnly));
    }

    [Fact]
    public void ConversationOwnerSupportsMappedClaimsAndNormalizesGuids()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("http://schemas.microsoft.com/identity/claims/tenantid", Tenant.ToUpperInvariant()),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", Alice)
        }, "Test"));

        Assert.Equal($"{Tenant}:{Alice}", WebConversationIdentity.GetOwnerId(user));
    }

    private static async Task AssertAuthenticationErrorAsync(
        WebDevOpsTokenService service, int statusCode, string errorCode)
    {
        var exception = await Assert.ThrowsAsync<WebAuthenticationException>(
            () => service.GetTokenAsync());
        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.False(exception.Message.Contains(OpaqueToken, StringComparison.Ordinal));
    }

    private static IConfiguration Configuration(string? tenant, string? client, string? instance)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:TenantId"] = tenant,
            ["AzureAd:ClientId"] = client,
            ["AzureAd:Instance"] = instance
        }).Build();

    private static ClaimsPrincipal UserWithIdentity(string objectId = Alice)
        => new(new ClaimsIdentity(new[]
        {
            new Claim("oid", objectId),
            new Claim("tid", Tenant),
            new Claim("preferred_username", "alice@example.com")
        }, "Test"));

    private static DefaultHttpContext ContextWithIdentity()
    {
        var context = new DefaultHttpContext { User = UserWithIdentity() };
        context.Request.Headers[WebDevOpsTokenService.TokenHeader] = OpaqueToken;
        return context;
    }

    private static string GraphIdentity(string tenant, string objectId)
        => JsonSerializer.Serialize(new { origin = "aad", domain = tenant, originId = objectId });

    private sealed class TokenFixture : IDisposable
    {
        public DefaultHttpContext Context { get; } = ContextWithIdentity();
        public IHttpContextAccessor Accessor { get; }
        public IdentityHandler Handler { get; } = new();
        public HttpClient HttpClient { get; }
        public WebDevOpsTokenService Service { get; }

        public TokenFixture()
        {
            Accessor = new HttpContextAccessor { HttpContext = Context };
            HttpClient = new HttpClient(Handler);
            Service = new WebDevOpsTokenService(
                HttpClient, Accessor, NullLogger<WebDevOpsTokenService>.Instance);
        }

        public void Dispose() => HttpClient.Dispose();
    }

    private sealed record IdentityRequest(
        HttpMethod Method, Uri Uri, string? Scheme, string? Token, CancellationToken CancellationToken);

    private sealed class IdentityHandler : HttpMessageHandler
    {
        public List<IdentityRequest> Requests { get; } = new();
        public string ConnectionJson { get; set; } =
            JsonSerializer.Serialize(new { authenticatedUser = new { subjectDescriptor = Descriptor } });
        public string GraphJson { get; set; } = GraphIdentity(Tenant, Alice);
        public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>? OnSend { get; set; }

        public HttpResponseMessage IdentityResponse(int call)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    call % 2 == 1 ? ConnectionJson : GraphJson, Encoding.UTF8, "application/json")
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new IdentityRequest(
                request.Method, request.RequestUri!, request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter, cancellationToken));
            return OnSend?.Invoke(request, Requests.Count, cancellationToken)
                ?? Task.FromResult(IdentityResponse(Requests.Count));
        }
    }
}
