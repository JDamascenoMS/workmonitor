using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using WorkMonitor.Mcp.Http;

namespace WorkMonitor.Tests;

public sealed class HttpMcpOboTests
{
    [Theory]
    [InlineData("EntraObo")]
    [InlineData("DevOps")]
    [InlineData("ThemeModel")]
    [InlineData("ModelIdentity")]
    public async Task ProductionHttpClientsNeverReplayCookiesAcrossCallersOrFollowRedirects(string name)
    {
        var requests = new ConcurrentQueue<(string Authorization, string Cookie)>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        await using var source = builder.Build();
        source.Run(context =>
        {
            requests.Enqueue((context.Request.Headers.Authorization.ToString(), context.Request.Headers.Cookie.ToString()));
            context.Response.Headers.SetCookie = "user-session=fixture; Path=/; HttpOnly";
            if (context.Request.Path == "/redirect")
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = "/must-not-follow";
            }
            return context.Response.WriteAsync("local fixture", context.RequestAborted);
        });
        await source.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var app = new HttpMcpFactory(productionHttpHandlers: true);
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();
        foreach (var user in new[] { "A", "B", "A" })
        {
            using var client = factory.CreateClient(name);
            client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-user-" + user);
            using var response = await client.GetAsync(source.Urls.Single(), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var redirectClient = factory.CreateClient(name);
        using var redirect = await redirectClient.GetAsync(source.Urls.Single() + "/redirect", timeout.Token);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(new[] { "Bearer fixture-user-A", "Bearer fixture-user-B", "Bearer fixture-user-A", "" },
            requests.Select(request => request.Authorization));
        Assert.All(requests, request => Assert.Empty(request.Cookie));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionMsalUsesConfidentialOboAndDevOpsScopeWithoutTokenPersistence(bool certificateCredential)
    {
        using var directory = new TestDataDirectory("http-obo-credential");
        var config = Configuration(directory.Path);
        if (certificateCredential)
        {
            config["WorkMonitor:HttpMcp:ClientSecret"] = null;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Fixture only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
            var path = Path.Combine(directory.Path, "test-credential.pfx");
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
            config["WorkMonitor:HttpMcp:CertificatePath"] = path;
        }
        using var settings = HttpMcpSettings.Load(config);
        using var identity = new MsalIdentitySource();
        var services = new ServiceCollection();
        services.AddHttpClient("EntraObo").ConfigurePrimaryHttpMessageHandler(() => identity);
        using var provider = services.BuildServiceProvider();
        var acquirer = new DevOpsOboTokenAcquirer(settings, provider.GetRequiredService<IHttpClientFactory>());
        Assert.Equal("opaque-devops-token", await acquirer.AcquireAsync("fixture-user-assertion-A", default));
        Assert.Equal("opaque-devops-token", await acquirer.AcquireAsync("fixture-user-assertion-B", default));
        Assert.Equal("opaque-devops-token", await acquirer.AcquireAsync("fixture-user-assertion-A", default));
        var requests = identity.TokenRequests.ToArray();
        Assert.Equal(3, requests.Length);
        foreach (var request in requests)
        {
            Assert.Equal(HttpMcpTests.ClientId, request["client_id"]);
            Assert.Equal(new[] { DevOpsOboTokenAcquirer.Scope, "offline_access", "openid", "profile" },
                request["scope"].Split(' ').OrderBy(scope => scope));
            Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", request["grant_type"]);
            Assert.Equal("on_behalf_of", request["requested_token_use"]);
            Assert.Equal(certificateCredential, request.ContainsKey("client_assertion"));
            Assert.Equal(!certificateCredential, request.ContainsKey("client_secret"));
            Assert.Contains("cp1", request["claims"]);
        }
        Assert.Equal(new[] { "fixture-user-assertion-A", "fixture-user-assertion-B", "fixture-user-assertion-A" }, requests.Select(request => request["assertion"]));
        Assert.All(identity.Urls, url =>
        {
            Assert.Equal("https", url.Scheme);
            Assert.Equal("login.microsoftonline.com", url.Host);
        });
        Assert.All(identity.Urls.Where(url => url.AbsolutePath.EndsWith("/token")), url =>
            Assert.Equal($"/{HttpMcpTests.Tenant}/oauth2/v2.0/token", url.AbsolutePath));
    }

    [Theory]
    [InlineData("TenantId", "")]
    [InlineData("TenantId", "common")]
    [InlineData("ClientId", "")]
    [InlineData("RequiredScope", "")]
    [InlineData("RequiredScope", "access_as_user another")]
    [InlineData("DataDirectory", "relative")]
    [InlineData("ClientSecret", "")]
    [InlineData("CertificatePath", "relative.pfx")]
    public void MissingOrInvalidConfigurationFailsWithoutLeakingCredentialValues(string key, string value)
    {
        using var directory = new TestDataDirectory("http-invalid-config");
        var config = Configuration(directory.Path);
        config[$"WorkMonitor:HttpMcp:{key}"] = value;
        var error = Assert.Throws<InvalidOperationException>(() => HttpMcpSettings.Load(config));
        Assert.DoesNotContain(HttpMcpTests.Sentinel, error.ToString());
    }

    [Fact]
    public void InvalidCertificateDoesNotFallbackToSecretOrDesktopAuthentication()
    {
        using var directory = new TestDataDirectory("http-invalid-certificate");
        var config = Configuration(directory.Path);
        config["WorkMonitor:HttpMcp:CertificatePath"] = Path.Combine(directory.Path, "missing.pfx");
        var both = Assert.Throws<InvalidOperationException>(() => HttpMcpSettings.Load(config));
        Assert.Contains("exactly one", both.Message);
        config["WorkMonitor:HttpMcp:ClientSecret"] = null;
        var missing = Assert.Throws<InvalidOperationException>(() => HttpMcpSettings.Load(config));
        Assert.Contains("private key", missing.Message);
        Assert.DoesNotContain(directory.Path, missing.Message);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("no-private-key")]
    [InlineData("wrong-password")]
    public void UnusableCertificateFailsStartupWithoutRevealingSecretOrPath(string mode)
    {
        using var directory = new TestDataDirectory("http-unusable-certificate");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Fixture only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(mode == "future" ? 5 : -10),
            DateTimeOffset.UtcNow.AddMinutes(mode == "expired" ? -5 : 10));
        var path = Path.Combine(directory.Path, "unusable.pfx");
        File.WriteAllBytes(path, mode == "no-private-key" ? certificate.Export(X509ContentType.Cert) :
            certificate.Export(X509ContentType.Pfx, mode == "wrong-password" ? HttpMcpTests.Sentinel : ""));
        var config = Configuration(directory.Path);
        config["WorkMonitor:HttpMcp:ClientSecret"] = null;
        config["WorkMonitor:HttpMcp:CertificatePath"] = path;
        var error = Assert.Throws<InvalidOperationException>(() => HttpMcpSettings.Load(config));
        Assert.Contains("private key", error.Message);
        Assert.DoesNotContain(HttpMcpTests.Sentinel, error.ToString());
        Assert.DoesNotContain(path, error.ToString());
    }

    [Fact]
    public async Task ProductionOboCancellationReachesMsalTokenEndpoint()
    {
        using var directory = new TestDataDirectory("http-cancel-obo");
        using var settings = HttpMcpSettings.Load(Configuration(directory.Path));
        using var source = new MsalIdentitySource { Mode = "block" };
        var services = new ServiceCollection();
        services.AddHttpClient("EntraObo").ConfigurePrimaryHttpMessageHandler(() => source);
        using var provider = services.BuildServiceProvider();
        var acquirer = new DevOpsOboTokenAcquirer(settings, provider.GetRequiredService<IHttpClientFactory>());
        using var cancellation = new CancellationTokenSource();
        var pending = acquirer.AcquireAsync("fixture-user-assertion", cancellation.Token);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static IConfigurationRoot Configuration(string directory)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkMonitor:HttpMcp:TenantId"] = HttpMcpTests.Tenant,
            ["WorkMonitor:HttpMcp:ClientId"] = HttpMcpTests.ClientId,
            ["WorkMonitor:HttpMcp:RequiredScope"] = "access_as_user",
            ["WorkMonitor:HttpMcp:DataDirectory"] = directory,
            ["WorkMonitor:HttpMcp:ClientSecret"] = HttpMcpTests.Sentinel
        }).Build();

    private sealed class TestDataDirectory : IDisposable
    {
        public string Path { get; }
        public TestDataDirectory(string category)
        {
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, category, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

internal sealed class MsalIdentitySource : HttpMessageHandler
{
    public string? Mode { get; set; }
    public string? Claims { get; set; }
    public Func<string, string>? TokenForAssertion { get; set; }
    public ConcurrentQueue<Dictionary<string, string>> TokenRequests { get; } = new();
    public ConcurrentQueue<Uri> Urls { get; } = new();
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        Urls.Enqueue(url);
        Assert.Equal("login.microsoftonline.com", url.Host);
        if (url.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
        {
            var body = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            TokenRequests.Enqueue(body.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
            Entered.TrySetResult();
            if (Mode == "block") await Task.Delay(Timeout.Infinite, cancellationToken);
            return Mode == "interaction"
                ? Json(HttpStatusCode.BadRequest, new { error = "invalid_grant",
                    error_description = HttpMcpTests.Sentinel, suberror = "basic_action", claims = Claims })
                : Json(HttpStatusCode.OK, new { access_token = TokenForAssertion?.Invoke(body["assertion"].ToString()) ?? "opaque-devops-token", token_type = "Bearer",
                    expires_in = 3600, scope = DevOpsOboTokenAcquirer.Scope });
        }
        if (url.AbsolutePath.EndsWith("/discovery/instance", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new
            {
                tenant_discovery_endpoint = $"{HttpMcpTests.Issuer}/.well-known/openid-configuration",
                metadata = new[] { new { preferred_network = "login.microsoftonline.com",
                    preferred_cache = "login.windows.net", aliases = new[] { "login.microsoftonline.com", "login.windows.net" } } }
            });
        Assert.EndsWith("/.well-known/openid-configuration", url.AbsolutePath);
        return Json(HttpStatusCode.OK, new
        {
            issuer = HttpMcpTests.Issuer,
            authorization_endpoint = $"https://login.microsoftonline.com/{HttpMcpTests.Tenant}/oauth2/v2.0/authorize",
            token_endpoint = $"https://login.microsoftonline.com/{HttpMcpTests.Tenant}/oauth2/v2.0/token",
            jwks_uri = $"https://login.microsoftonline.com/{HttpMcpTests.Tenant}/discovery/v2.0/keys"
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
