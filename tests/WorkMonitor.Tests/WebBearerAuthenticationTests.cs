using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FT_MONITOR.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace WorkMonitor.Tests;

public sealed class WebBearerAuthenticationTests
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string ObjectId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";
    private const string Issuer = "https://login.microsoftonline.com/" + Tenant + "/v2.0";
    private const string DevOpsAudience = "499b84ac-1321-427f-aa17-267ca6975798";

    [Fact]
    public async Task PublicStatusReturnsSecretlessSpaPkceConfiguration()
    {
        await using var app = new BearerFactory();
        using var client = app.Client();

        using var result = await client.GetAsync("/api/auth/status");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        Assert.Equal("spaPkce", body.GetProperty("mode").GetString());
        Assert.Equal(ClientId, body.GetProperty("clientId").GetString());
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}", body.GetProperty("authority").GetString());
        Assert.Equal($"api://{ClientId}/access_as_user", body.GetProperty("apiScope").GetString());
        Assert.Equal(WebDevOpsTokenService.DelegatedScope, body.GetProperty("devOpsScope").GetString());
        Assert.Equal("/auth-callback.html", body.GetProperty("redirectPath").GetString());
        Assert.False(body.TryGetProperty("clientSecret", out _));
        Assert.False(body.TryGetProperty("clientCertificates", out _));
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        Assert.True(string.IsNullOrEmpty(configuration["AzureAd:ClientSecret"]));
        Assert.True(string.IsNullOrEmpty(configuration["AzureAd:ClientCertificates:0:CertificatePath"]));
    }

    [Fact]
    public async Task SpaCallbackIsPublicUncachedAndPreservesPopupOpener()
    {
        await using var app = new BearerFactory();
        using var client = app.Client();

        using var result = await client.GetAsync("/auth-callback.html?code=test-response");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(result.Headers.CacheControl?.NoStore);
        Assert.False(result.Headers.Contains("Cross-Origin-Opener-Policy"));
    }

    [Fact]
    public async Task ProductionBearerSchemeAndValidationArePreserved()
    {
        await using var app = new BearerFactory();
        var options = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        var authentication = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        var schemes = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();

        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, authentication.DefaultScheme);
        Assert.Single(schemes);
        Assert.Equal(typeof(JwtBearerHandler), schemes.Single().HandlerType);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
        Assert.True(options.TokenValidationParameters.RequireSignedTokens);
        Assert.NotNull(options.TokenValidationParameters.AudienceValidator);
        Assert.NotNull(options.TokenValidationParameters.IssuerValidator);
        Assert.IsType<StaticConfigurationManager<OpenIdConnectConfiguration>>(options.ConfigurationManager);
    }

    [Fact]
    public async Task OwnApiDelegatedBearerAuthenticatesMeAndController()
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token());

        using var me = await client.GetAsync("/api/me");
        var identity = await me.Content.ReadFromJsonAsync<JsonElement>();
        using var tools = await client.GetAsync("/api/agent/tools");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("Alice", identity.GetProperty("name").GetString());
        Assert.Equal("alice@example.com", identity.GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);
    }

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/agent/tools")]
    [InlineData("/api/top-bugs")]
    public async Task AnonymousProtectedEndpointsRejectWithoutCookieRedirect(string path)
    {
        await using var app = new BearerFactory();
        using var client = app.Client();

        using var result = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Null(result.Headers.Location);
        Assert.Contains(result.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Fact]
    public async Task AnonymousConversationCreationIsRejected()
    {
        await using var app = new BearerFactory();
        using var client = app.Client();

        using var result = await client.PostAsJsonAsync("/api/agent/conversations", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Null(result.Headers.Location);
    }

    [Theory]
    [InlineData(DevOpsAudience)]
    [InlineData("api://" + DevOpsAudience)]
    [InlineData("44444444-4444-4444-4444-444444444444")]
    public async Task OtherResourceAudiencesCannotAuthenticateOwnApi(string audience)
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token(audience: audience));

        using var me = await client.GetAsync("/api/me");
        using var tools = await client.GetAsync("/api/agent/tools");

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tools.StatusCode);
    }

    [Theory]
    [InlineData(null, false, HttpStatusCode.Unauthorized)]
    [InlineData(null, true, HttpStatusCode.Forbidden)]
    [InlineData("user_impersonation", false, HttpStatusCode.Forbidden)]
    [InlineData("access_as_user_extra", false, HttpStatusCode.Forbidden)]
    [InlineData("ACCESS_AS_USER", false, HttpStatusCode.Forbidden)]
    public async Task IdApplicationAndWrongScopeTokensAreRejected(
        string? scope, bool applicationToken, HttpStatusCode expectedStatus)
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token(scope: scope, applicationToken: applicationToken));

        using var me = await client.GetAsync("/api/me");
        using var tools = await client.GetAsync("/api/agent/tools");
        using var create = await client.PostAsJsonAsync("/api/agent/conversations", new { });

        Assert.Equal(expectedStatus, me.StatusCode);
        Assert.Equal(expectedStatus, tools.StatusCode);
        Assert.Equal(expectedStatus, create.StatusCode);
    }

    [Fact]
    public async Task DelegatedScopeListContainingExactOwnApiScopeIsAccepted()
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token(scope: "other access_as_user another"));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/44444444-4444-4444-4444-444444444444/v2.0")]
    [InlineData("https://untrusted.example/" + Tenant + "/v2.0")]
    public async Task WrongIssuerCannotAuthenticate(string issuer)
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token(issuer: issuer));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task WrongSigningKeyCannotAuthenticate()
    {
        await using var app = new BearerFactory();
        using var otherRsa = RSA.Create(2048);
        var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "local-test-key" };
        using var client = app.Client(app.Token(signingKey: otherKey));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task UnsignedTokenCannotAuthenticate()
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token(unsigned: true));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task ExpiredTokenCannotAuthenticate()
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token(expires: DateTime.UtcNow.AddHours(-1)));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task NotYetValidTokenCannotAuthenticate()
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token(notBefore: DateTime.UtcNow.AddHours(1)));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task CookiesAloneCannotAuthenticateEvenWhenContainingValidBearerToken()
    {
        await using var app = new BearerFactory();
        using var client = app.Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie", ".AspNetCore.Cookies=" + app.Token());

        using var me = await client.GetAsync("/api/me");
        using var tools = await client.GetAsync("/api/agent/tools");

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tools.StatusCode);
        Assert.Empty(me.Headers.Where(header => header.Key == "Set-Cookie"));
    }

    [Fact]
    public async Task DownstreamTokenHeaderAloneCannotAuthenticateOwnApi()
    {
        await using var app = new BearerFactory();
        using var client = app.Client();
        client.DefaultRequestHeaders.Add(WebDevOpsTokenService.TokenHeader, "opaque-resource-token");

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task ValidApiBearerWithoutResourceTokenGetsActionableErrorWithoutHostFallback()
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token());

        using var result = await client.GetAsync("/api/top-bugs");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("devops_token_required", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task MalformedBearerTokenIsRejected()
    {
        await using var app = new BearerFactory();
        using var client = app.Client("not-a-jwt");

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task SpaBearerCannotEnableLocalCookieCsrfOrSignOutEndpoints()
    {
        await using var app = new BearerFactory();
        using var client = app.Client(app.Token());
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-CSRF-TOKEN", "not-a-spa-requirement");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "WorkMonitor.LocalDevelopment=unused");

        using var csrf = await client.GetAsync("/api/agent/csrf");
        using var signOut = await client.PostAsJsonAsync("/api/auth/local-sign-out", new { });
        using var me = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.NotFound, csrf.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    private sealed class BearerFactory : WebApplicationFactory<FT_MONITOR.Program>
    {
        private readonly string _directory = Path.Combine(
            AppContext.BaseDirectory, "web-bearer-test-data", Guid.NewGuid().ToString("N"));
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RsaSecurityKey _key;

        public BearerFactory()
        {
            _key = new RsaSecurityKey(_rsa) { KeyId = "local-test-key" };
        }

        public HttpClient Client(string? bearer = null)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false
            });
            if (bearer is not null)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return client;
        }

        public string Token(
            string audience = ClientId,
            string issuer = Issuer,
            string? scope = "access_as_user",
            bool applicationToken = false,
            SecurityKey? signingKey = null,
            bool unsigned = false,
            DateTime? expires = null,
            DateTime? notBefore = null)
        {
            var claims = new List<Claim>
            {
                new("tid", Tenant),
                new("oid", ObjectId),
                new("ver", "2.0"),
                new("name", "Alice"),
                new("preferred_username", "alice@example.com")
            };
            if (scope is not null)
                claims.Add(new Claim("scp", scope));
            if (applicationToken)
            {
                claims.Add(new Claim("idtyp", "app"));
                claims.Add(new Claim("roles", "access_as_user"));
            }
            var token = new JwtSecurityToken(
                issuer, audience, claims,
                notBefore ?? DateTime.UtcNow.AddHours(-2),
                expires ?? DateTime.UtcNow.AddHours(2),
                unsigned ? null : new SigningCredentials(signingKey ?? _key, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("WorkMonitor:Web:DataDirectory", _directory);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                    ["AzureAd:TenantId"] = Tenant,
                    ["AzureAd:ClientId"] = ClientId,
                    ["AzureAd:ClientSecret"] = null,
                    ["AzureAd:ClientCertificates:0:CertificatePath"] = null,
                    ["AzureOpenAI:Endpoint"] = "https://unused-openai.example/",
                    ["AzureOpenAI:DeploymentName"] = "unused-test-deployment",
                    ["AzureOpenAI:ApiKey"] = "test-only-unused-api-key"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.Configure<Microsoft.Identity.Web.AadIssuerValidatorOptions>(
                    options => options.HttpClientName = "offline-issuer-metadata");
                services.AddHttpClient("offline-issuer-metadata")
                    .ConfigurePrimaryHttpMessageHandler(() => new LocalIssuerMetadataHandler());
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    // Only substitute local metadata/key; retain Identity.Web's audience/issuer validators.
                    var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                    metadata.SigningKeys.Add(_key);
                    options.Configuration = metadata;
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.IssuerSigningKey = _key;
                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                    options.Backchannel = new HttpClient(new NoDiscoveryHandler());
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _rsa.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class NoDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Bearer tests must not perform live OIDC discovery.");
    }

    private sealed class LocalIssuerMetadataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { issuer = Issuer })
            });
    }
}
