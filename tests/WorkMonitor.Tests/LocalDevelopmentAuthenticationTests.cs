using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using FT_MONITOR.Middleware;
using FT_MONITOR.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Tests;

public sealed class LocalDevelopmentAuthenticationTests
{
    private const string Tenant = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string Alice = "11111111-1111-1111-1111-111111111111";
    private const string Bob = "22222222-2222-2222-2222-222222222222";
    private const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    private const string CookieName = "WorkMonitor.LocalDevelopment";

    [Theory]
    [InlineData(null, WebAuthenticationMode.SpaPkce)]
    [InlineData("", WebAuthenticationMode.SpaPkce)]
    [InlineData("SpaPkce", WebAuthenticationMode.SpaPkce)]
    [InlineData("spapkce", WebAuthenticationMode.SpaPkce)]
    [InlineData("LocalDevelopment", WebAuthenticationMode.LocalDevelopment)]
    [InlineData("localdevelopment", WebAuthenticationMode.LocalDevelopment)]
    public void OnlyExplicitLocalModeChangesTheDefault(string? value, WebAuthenticationMode expected)
        => Assert.Equal(expected, WebAuthenticationModeConfiguration.GetMode(
            Configuration(("WorkMonitor:Web:AuthenticationMode", value))));

    [Theory]
    [InlineData("Production")]
    [InlineData("Local")]
    [InlineData(" ")]
    public void UnknownAuthenticationModeCannotSilentlyEnableAnotherMode(string value)
        => Assert.Throws<InvalidOperationException>(() =>
            WebAuthenticationModeConfiguration.GetMode(
                Configuration(("WorkMonitor:Web:AuthenticationMode", value))));

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void LocalModeRefusesNonDevelopmentStartup(string environment)
        => Assert.Throws<InvalidOperationException>(() =>
            LocalDevelopmentOnlyMiddleware.ValidateStartup(
                new TestEnvironment { EnvironmentName = environment },
                Configuration(), "https://localhost:7286"));

    [Theory]
    [InlineData("https://localhost:7286")]
    [InlineData("http://127.0.0.1:5191")]
    [InlineData("https://[::1]:7286")]
    [InlineData("https://localhost:7286;http://127.0.0.1:5191")]
    public void DevelopmentStartupAllowsOnlyDirectLoopbackListeners(string urls)
        => LocalDevelopmentOnlyMiddleware.ValidateStartup(
            new TestEnvironment(), Configuration(), urls);

    [Theory]
    [InlineData("")]
    [InlineData("https://0.0.0.0:7286")]
    [InlineData("http://*:5191")]
    [InlineData("http://+:5191")]
    [InlineData("https://[::]:7286")]
    [InlineData("https://remote.example:7286")]
    [InlineData("https://localhost:7286;http://192.0.2.10:5191")]
    [InlineData("ftp://localhost:7286")]
    [InlineData("https://username@localhost:7286")]
    [InlineData("https://localhost:7286/?query=value")]
    [InlineData("https://localhost:7286/#fragment")]
    [InlineData("https://localhost:7286/non-root")]
    public void NonLoopbackAndWildcardListenersAreRejected(string urls)
        => Assert.Throws<InvalidOperationException>(() =>
            LocalDevelopmentOnlyMiddleware.ValidateStartup(
                new TestEnvironment(), Configuration(), urls));

    [Fact]
    public void CustomKestrelEndpointsCannotBypassListenerValidation()
        => Assert.Throws<InvalidOperationException>(() =>
            LocalDevelopmentOnlyMiddleware.ValidateStartup(
                new TestEnvironment(),
                Configuration(("Kestrel:Endpoints:Public:Url", "https://localhost:7286")),
                "https://localhost:7286"));

    [Fact]
    public async Task ActualLocalWebHostCannotStartInProduction()
    {
        await using var app = new LocalFactory(environment: "Production");

        var error = Assert.Throws<InvalidOperationException>(() => app.Client(authenticated: false));

        Assert.Contains("Development", error.Message);
    }

    [Theory]
    [InlineData("44444444-4444-4444-4444-444444444444", "alice@example.com")]
    [InlineData("not-a-tenant", "alice@example.com")]
    [InlineData("00000000-0000-0000-0000-000000000000", "alice@example.com")]
    [InlineData(Tenant, null)]
    [InlineData(Tenant, " ")]
    public void LocalRegistrationRequiresMatchingTenantAndDeveloperHint(string tenant, string? hint)
    {
        var configuration = Configuration(
            ("AzureAd:TenantId", Tenant),
            ("WorkMonitor:Authentication:TenantId", tenant),
            ("WorkMonitor:Authentication:LoginHint", hint));

        Assert.Throws<InvalidOperationException>(() =>
            LocalDevelopmentAuthentication.Register(new ServiceCollection(), configuration, "unused"));
    }

    [Fact]
    public async Task LocalPublicStatusHasNoResourceScopesAndExplainsDedicatedFlow()
    {
        await using var app = new LocalFactory();
        using var client = app.Client(authenticated: false);

        using var result = await client.GetAsync("/api/auth/status");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("no-store", result.Headers.CacheControl?.ToString());
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal("localDevelopment", body.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("apiScope").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("devOpsScope").ValueKind);
        Assert.Equal("/signin-oidc", body.GetProperty("redirectPath").GetString());
        Assert.Equal("/MicrosoftIdentity/Account/SignIn", body.GetProperty("signInPath").GetString());
        Assert.Equal("/api/agent/csrf", body.GetProperty("csrfPath").GetString());
        Assert.Equal("/api/auth/local-sign-out", body.GetProperty("signOutPath").GetString());
        Assert.Contains("localhost", body.GetProperty("warning").GetString()!);
        Assert.Equal(0, app.Identity.IdentityCalls);
    }

    [Fact]
    public async Task LocalOidcUsesOnlyBasicIdentityScopesAndDedicatedSecureCookie()
    {
        await using var app = new LocalFactory();
        var oidc = app.OidcOptions;
        var cookie = app.CookieOptions;
        var authentication = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        var schemes = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();

        Assert.Equal(OpenIdConnectResponseType.IdToken, oidc.ResponseType);
        Assert.Equal(OpenIdConnectResponseMode.FormPost, oidc.ResponseMode);
        Assert.Equal("/signin-oidc", oidc.CallbackPath.Value);
        Assert.Equal(new[] { "email", "openid", "profile" }, oidc.Scope.OrderBy(scope => scope).ToArray());
        Assert.True(string.IsNullOrEmpty(oidc.ClientSecret));
        Assert.False(oidc.SaveTokens);
        Assert.Equal(CookieScheme, authentication.DefaultScheme);
        Assert.Equal(OidcScheme, authentication.DefaultChallengeScheme);
        Assert.DoesNotContain(schemes, scheme => scheme.Name == "Bearer");
        Assert.Equal(CookieName, cookie.Cookie.Name);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, cookie.Cookie.SameSite);
        Assert.Equal(TimeSpan.FromHours(2), cookie.ExpireTimeSpan);
        Assert.True(Directory.Exists(Path.Combine(app.DataDirectory, "LocalDpKeys")));
        Assert.False(app.DataDirectory.StartsWith(
            app.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath ?? "\0",
            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost")]
    [InlineData("::1", "localhost")]
    [InlineData("::ffff:127.0.0.1", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1:7286")]
    [InlineData("::1", "[::1]:7286")]
    public async Task RawLoopbackAddressAndHostAreAccepted(string remote, string host)
    {
        await using var app = new LocalFactory();
        using var client = app.Client(authenticated: false);
        client.DefaultRequestHeaders.Add("X-Test-Raw-Remote", remote);
        client.DefaultRequestHeaders.Add("X-Test-Raw-Host", host);

        using var result = await client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Theory]
    [InlineData("192.0.2.42", "localhost")]
    [InlineData("::ffff:192.0.2.42", "localhost")]
    [InlineData("none", "localhost")]
    [InlineData("127.0.0.1", "remote.example")]
    [InlineData("127.0.0.1", "localhost.remote.example")]
    [InlineData("127.0.0.1", "localhost@remote.example")]
    [InlineData("127.0.0.1", "none")]
    public async Task RawConnectionGuardRunsBeforeAuthAndIgnoresSpoofedForwardedHeaders(
        string remote, string host)
    {
        await using var app = new LocalFactory();
        using var client = app.Client();
        client.DefaultRequestHeaders.Add("X-Test-Raw-Remote", remote);
        client.DefaultRequestHeaders.Add("X-Test-Raw-Host", host);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "localhost");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        using var result = await client.GetAsync("/api/me");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("local_development_only", body.GetProperty("errorCode").GetString());
        Assert.Equal(0, app.Identity.IdentityCalls);
    }

    [Fact]
    public async Task ForwardedRemoteHostAndSchemeNeverReplaceTheLocalRawRequest()
    {
        await using var app = new LocalFactory();
        using var client = app.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.42");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "remote.example");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task HttpCannotUseLocalCookieBeforeHttpsRedirect()
    {
        await using var app = new LocalFactory();
        using var client = app.Client();

        using var result = await client.GetAsync("http://localhost:5191/api/me");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, result.StatusCode);
        Assert.Equal("https", result.Headers.Location?.Scheme);
        Assert.Equal(0, app.Identity.IdentityCalls);
    }

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/agent/conversations")]
    [InlineData("/api/agent/csrf")]
    [InlineData("/api/parameters/area-paths")]
    public async Task AnonymousLocalApisRejectWithoutOidcRedirect(string path)
    {
        await using var app = new LocalFactory();
        using var client = app.Client(authenticated: false);

        using var result = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Null(result.Headers.Location);
    }

    [Fact]
    public async Task RealLocalCookieAuthenticatesBoundDeveloperWithoutBearerScope()
    {
        await using var app = new LocalFactory();
        using var client = app.Client();

        using var result = await client.GetAsync("/api/me");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("localDevelopment", body.GetProperty("authMode").GetString());
        Assert.Equal("alice@example.com", body.GetProperty("devOpsIdentity").GetString());
        Assert.Equal("Alice", body.GetProperty("name").GetString());
        Assert.True(app.Identity.IdentityCalls > 0);
    }

    [Fact]
    public async Task VerifiedLocalCookieWithoutDisplayClaimsUsesCanonicalOwnerAsDeveloperLabel()
    {
        await using var app = new LocalFactory();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", Tenant),
            new Claim("oid", Alice),
            new Claim(LocalDevelopmentAuthentication.ModeClaim, LocalDevelopmentAuthentication.ModeValue)
        }, CookieScheme));
        using var client = app.Client(principal: principal);

        using var result = await client.GetAsync("/api/me");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        var label = body.GetProperty("devOpsIdentity").GetString();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("localDevelopment", body.GetProperty("authMode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.Equal($"{Tenant}:{Alice}", label);
        Assert.True(app.Identity.IdentityCalls > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("spaPkce")]
    public async Task CookieWithoutExactLocalModeClaimIsRejectedAndCleared(string? mode)
    {
        await using var app = new LocalFactory();
        using var client = app.Client(principal: User(mode: mode));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Contains(result.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(CookieName + "=;", StringComparison.Ordinal));
        Assert.Equal(0, app.Identity.IdentityCalls);
    }

    [Fact]
    public async Task AnotherOwnerCannotInheritDeveloperCookieOrConversations()
    {
        await using var app = new LocalFactory();
        using var alice = app.Client();
        await AddCsrfAsync(alice);
        using var created = await alice.PostAsJsonAsync("/api/agent/conversations", new { });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString();
        using var bob = app.Client(principal: User(objectId: Bob));

        using var result = await bob.GetAsync($"/api/agent/conversations/{id}");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Contains(result.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(CookieName + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangingCurrentDeveloperIdentityRejectsAnExistingCookie()
    {
        await using var app = new LocalFactory();
        using var client = app.Client();
        using var first = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        app.Identity.Identity = Identity(Bob);

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task CookieWithMalformedStableOwnerFailsWithoutAccessingData()
    {
        await using var app = new LocalFactory();
        using var client = app.Client(principal: User(objectId: "not-a-guid"));

        using var result = await client.GetAsync("/api/agent/conversations");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("identity_claims_missing", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ExpiredRealCookieCannotAuthenticate()
    {
        await using var app = new LocalFactory();
        using var client = app.Client(expires: DateTimeOffset.UtcNow.AddHours(-1));

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, app.Identity.IdentityCalls);
    }

    [Fact]
    public async Task LocalCookieCannotBeReplayedAgainstAnotherInstancesDedicatedKeys()
    {
        await using var first = new LocalFactory();
        using var firstClient = first.Client();
        var cookie = firstClient.DefaultRequestHeaders.GetValues("Cookie").Single();
        await using var second = new LocalFactory();
        using var secondClient = second.Client(authenticated: false);
        secondClient.DefaultRequestHeaders.Add("Cookie", cookie);

        using var result = await secondClient.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, second.Identity.IdentityCalls);
    }

    [Fact]
    public async Task DeveloperCacheFailureDuringCookieValidationIsActionable503()
    {
        await using var app = new LocalFactory();
        using var client = app.Client();
        app.Identity.Failure = new WebAuthenticationException(
            503, "local_developer_login_required", "Run workmonitor --auth-login.");

        using var result = await client.GetAsync("/api/me");
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal("local_developer_login_required", body.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData("POST", "/api/agent/conversations")]
    [InlineData("POST", "/api/agent/run")]
    [InlineData("POST", "/api/agent/conversations/missing/stream")]
    [InlineData("POST", "/api/agent/skills")]
    [InlineData("DELETE", "/api/agent/skills/missing")]
    [InlineData("POST", "/api/parameters/area-paths")]
    [InlineData("PUT", "/api/parameters/area-paths/missing")]
    [InlineData("DELETE", "/api/parameters/area-paths/missing")]
    [InlineData("POST", "/api/parameters/saved-queries")]
    [InlineData("PUT", "/api/parameters/saved-queries/missing")]
    [InlineData("DELETE", "/api/parameters/saved-queries/missing")]
    [InlineData("POST", "/api/auth/local-sign-out")]
    public async Task EveryUnsafeLocalControllerRequiresAntiforgery(string method, string path)
    {
        await using var app = new LocalFactory();
        using var client = app.Client();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new
            {
                prompt = "Do not invoke a model", name = "Unused", path = "Unused",
                queryId = "Unused", dryRun = true
            })
        };

        using var result = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal(0, app.Identity.TokenCalls);
    }

    [Fact]
    public async Task LocalCsrfAllowsConversationCreationButRejectsWrongToken()
    {
        await using var app = new LocalFactory();
        using var client = app.Client();
        await AddCsrfAsync(client);

        using var created = await client.PostAsJsonAsync("/api/agent/conversations", new { });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid-antiforgery-token");
        using var rejected = await client.PostAsJsonAsync("/api/agent/conversations", new { });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task LocalSignOutClearsOnlyWebCookieAndDoesNotResetDeveloperCache()
    {
        await using var app = new LocalFactory();
        using var client = app.Client();
        var recordPath = Path.Combine(app.DataDirectory, "authentication-record.json");
        await File.WriteAllTextAsync(recordPath, "untouched-cli-cache-record");
        await AddCsrfAsync(client);

        using var result = await client.PostAsJsonAsync("/api/auth/local-sign-out", new { });
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("Cookie");
        using var after = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True(body.GetProperty("signedOut").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal("untouched-cli-cache-record", await File.ReadAllTextAsync(recordPath));
        var cleared = result.Headers.GetValues("Set-Cookie").Single(value =>
            value.StartsWith(CookieName + "=;", StringComparison.Ordinal));
        Assert.Contains("secure", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cleared, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OidcAddsModeClaimOnlyAfterVerifiedDeveloperOwnerMatches()
    {
        await using var app = new LocalFactory();
        using var scope = app.Services.CreateScope();
        var user = User(mode: null);
        var context = OidcContext(app, scope.ServiceProvider, user);

        await app.OidcOptions.Events.OnTokenValidated(context);

        Assert.True(user.HasClaim(LocalDevelopmentAuthentication.ModeClaim,
            LocalDevelopmentAuthentication.ModeValue));
        Assert.Equal(1, app.Identity.IdentityCalls);
    }

    [Theory]
    [InlineData(Bob, Tenant)]
    [InlineData(Alice, "44444444-4444-4444-4444-444444444444")]
    public async Task OidcWrongOwnerCannotIssueLocalModeClaim(string objectId, string tenant)
    {
        await using var app = new LocalFactory();
        using var scope = app.Services.CreateScope();
        var user = User(objectId, tenant, mode: null);
        var context = OidcContext(app, scope.ServiceProvider, user);

        var error = await Assert.ThrowsAsync<WebAuthenticationException>(
            () => app.OidcOptions.Events.OnTokenValidated(context));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("local_developer_mismatch", error.ErrorCode);
        Assert.False(user.HasClaim(claim => claim.Type == LocalDevelopmentAuthentication.ModeClaim));
    }

    [Fact]
    public async Task OidcForwardsCallerCancellationToDeveloperVerification()
    {
        await using var app = new LocalFactory();
        using var scope = app.Services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var context = OidcContext(app, scope.ServiceProvider, User(mode: null));
        context.HttpContext.RequestAborted = cancellation.Token;

        await app.OidcOptions.Events.OnTokenValidated(context);

        Assert.Equal(cancellation.Token, app.Identity.LastCancellation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OidcRemoteFailurePreservesKnownDeveloperLoginError(bool wrapped)
    {
        await using var app = new LocalFactory();
        using var scope = app.Services.CreateScope();
        using var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = body;
        var failure = new WebAuthenticationException(
            503, "local_developer_login_required", "Run workmonitor --auth-login.");
        var context = new RemoteFailureContext(http,
            new AuthenticationScheme(OidcScheme, OidcScheme, typeof(OpenIdConnectHandler)), app.OidcOptions,
            wrapped ? new AuthenticationFailureException("OIDC wrapper", failure) : failure);

        await app.OidcOptions.Events.OnRemoteFailure(context);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);

        Assert.Equal(503, http.Response.StatusCode);
        Assert.Equal("local_developer_login_required",
            document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("Run workmonitor --auth-login.",
            document.RootElement.GetProperty("error").GetString());
        Assert.True(context.Result?.Handled);
    }

    [Fact]
    public async Task OidcUnknownRemoteFailureDoesNotExposeUnderlyingDetails()
    {
        await using var app = new LocalFactory();
        using var scope = app.Services.CreateScope();
        using var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = body;
        var context = new RemoteFailureContext(http,
            new AuthenticationScheme(OidcScheme, OidcScheme, typeof(OpenIdConnectHandler)), app.OidcOptions,
            new AuthenticationFailureException("sensitive provider details"));

        await app.OidcOptions.Events.OnRemoteFailure(context);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);

        Assert.Equal(403, http.Response.StatusCode);
        Assert.Equal("local_sign_in_failed", document.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("sensitive provider details",
            document.RootElement.GetProperty("error").GetString());
        Assert.True(context.Result?.Handled);
    }

    [Fact]
    public async Task OidcMalformedOwnerCannotReceiveLocalModeClaim()
    {
        await using var app = new LocalFactory();
        using var scope = app.Services.CreateScope();
        var user = User(objectId: "not-a-guid", mode: null);
        var context = OidcContext(app, scope.ServiceProvider, user);

        var error = await Assert.ThrowsAsync<WebAuthenticationException>(
            () => app.OidcOptions.Events.OnTokenValidated(context));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("identity_claims_missing", error.ErrorCode);
        Assert.False(user.HasClaim(claim => claim.Type == LocalDevelopmentAuthentication.ModeClaim));
    }

    [Fact]
    public async Task BearerAndResourceTokenHeadersCannotReplaceLocalCookie()
    {
        await using var app = new LocalFactory();
        using var client = app.Client(authenticated: false);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-local-cookie");
        client.DefaultRequestHeaders.Add(WebDevOpsTokenService.TokenHeader, "opaque-browser-resource-token");

        using var result = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, app.Identity.IdentityCalls);
    }

    [Fact]
    public async Task LocalTokenServicePassesVerifiedApiPrincipalAndCancellation()
    {
        var user = User();
        var identity = new FakeIdentity();
        var service = new LocalDevelopmentDevOpsTokenService(
            identity, new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        using var cancellation = new CancellationTokenSource();

        await service.GetTokenAsync(cancellation.Token);

        Assert.Same(user, identity.LastUser);
        Assert.Equal(cancellation.Token, identity.LastCancellation);
    }

    [Fact]
    public async Task DeveloperIdentityCacheIsBoundToExactOpaqueResourceToken()
    {
        using var fixture = new DeveloperIdentityFixture();
        var first = await fixture.Identity.GetIdentityAsync(CancellationToken.None);
        var repeated = await fixture.Identity.GetIdentityAsync(CancellationToken.None);
        Assert.Same(first, repeated);
        Assert.Equal(2, fixture.Handler.Calls);
        fixture.Credential.Token = "rotated-opaque-developer-token";
        fixture.Handler.Owner = Identity(Bob);

        var changed = await fixture.Identity.GetIdentityAsync(CancellationToken.None);

        Assert.Equal(Identity(Bob).OwnerId, changed.OwnerId);
        Assert.Equal(4, fixture.Handler.Calls);
        Assert.Equal(3, fixture.Credential.Calls);
    }

    [Theory]
    [InlineData(Bob, Tenant)]
    [InlineData(Alice, "44444444-4444-4444-4444-444444444444")]
    public async Task ProductionDeveloperIdentityRejectsAnotherGuidOwner(string objectId, string tenant)
    {
        using var fixture = new DeveloperIdentityFixture();

        var error = await Assert.ThrowsAsync<WebAuthenticationException>(
            () => fixture.Identity.GetTokenForUserAsync(
                User(objectId, tenant), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("local_developer_mismatch", error.ErrorCode);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("unavailable")]
    [InlineData("failed")]
    public async Task CachedCredentialFailuresNeverFallBackOrExposeCredentialDetails(string failure)
    {
        using var fixture = new DeveloperIdentityFixture();
        fixture.Credential.Failure = failure switch
        {
            "required" => new AuthenticationRequiredException(
                "private credential details", new TokenRequestContext(new[] { "unused" })),
            "unavailable" => new CredentialUnavailableException("private credential details"),
            _ => new AuthenticationFailedException("private credential details")
        };

        var error = await Assert.ThrowsAsync<WebAuthenticationException>(
            () => fixture.Identity.GetIdentityAsync(CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("local_developer_login_required", error.ErrorCode);
        Assert.DoesNotContain("private credential details", error.Message);
        Assert.Equal(1, fixture.Credential.Calls);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task MissingRealCachedCredentialProduces503WithoutNetworkOrInteractiveLogin()
    {
        using var directory = new TestDirectory();
        var credential = new PersistentInteractiveCredential(new PersistentInteractiveCredentialOptions(
            Tenant, "alice@example.com", "WorkMonitor.Tests.LocalDevelopment",
            Path.Combine(directory.Path, "missing-authentication-record.json"),
            AllowInteractiveAuthentication: false));
        using var handler = new IdentityHandler();
        using var http = new HttpClient(handler);
        var identity = new LocalDevelopmentIdentity(
            new DevOpsTokenService(credential),
            new WebDevOpsIdentityVerifier(http, NullLogger.Instance),
            NullLogger<LocalDevelopmentIdentity>.Instance);

        var error = await Assert.ThrowsAsync<WebAuthenticationException>(
            () => identity.GetIdentityAsync(CancellationToken.None));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("local_developer_login_required", error.ErrorCode);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DeveloperCredentialAndBothVerificationCallsHonorCancellation()
    {
        using var fixture = new DeveloperIdentityFixture();
        using var cancellation = new CancellationTokenSource();
        await fixture.Identity.GetIdentityAsync(cancellation.Token);
        Assert.Equal(cancellation.Token, fixture.Credential.LastCancellation);
        Assert.All(fixture.Handler.Cancellations, token => Assert.True(token.CanBeCanceled));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Identity.GetIdentityAsync(cancellation.Token));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    private static async Task AddCsrfAsync(HttpClient client)
    {
        using var result = await client.GetAsync("/api/agent/csrf");
        result.EnsureSuccessStatusCode();
        Assert.True(result.Headers.CacheControl?.NoStore);
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", body.GetProperty("requestToken").GetString());
    }

    private static Microsoft.AspNetCore.Authentication.OpenIdConnect.TokenValidatedContext OidcContext(
        LocalFactory app, IServiceProvider services, ClaimsPrincipal user)
        => new(new DefaultHttpContext { RequestServices = services },
            new AuthenticationScheme(OidcScheme, OidcScheme, typeof(OpenIdConnectHandler)), app.OidcOptions,
            user, new AuthenticationProperties())
        {
            Principal = user,
            SecurityToken = new JwtSecurityToken(claims: user.Claims),
            ProtocolMessage = new OpenIdConnectMessage(),
            Properties = new AuthenticationProperties()
        };

    private static ClaimsPrincipal User(
        string objectId = Alice, string tenant = Tenant, string? mode = LocalDevelopmentAuthentication.ModeValue)
    {
        var claims = new List<Claim>
        {
            new("oid", objectId), new("tid", tenant),
            new("name", "Alice"), new("preferred_username", "alice@example.com"),
            new(ClaimTypes.NameIdentifier, objectId)
        };
        if (mode is not null)
            claims.Add(new Claim(LocalDevelopmentAuthentication.ModeClaim, mode));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme));
    }

    private static VerifiedDevOpsIdentity Identity(string objectId = Alice)
        => new(Guid.Parse(Tenant), Guid.Parse(objectId));

    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(value => value.Key, value => value.Value)).Build();

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "WorkMonitor.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            AppContext.BaseDirectory, "local-development-test-data", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class LocalFactory : WebApplicationFactory<FT_MONITOR.Program>
    {
        private readonly TestDirectory _directory = new();
        private readonly string _environment;
        public string DataDirectory => _directory.Path;
        public FakeIdentity Identity { get; } = new();
        public CookieAuthenticationOptions CookieOptions => Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieScheme);
        public OpenIdConnectOptions OidcOptions => Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OidcScheme);

        public LocalFactory(string environment = "Development") => _environment = environment;

        public HttpClient Client(
            bool authenticated = true, ClaimsPrincipal? principal = null, DateTimeOffset? expires = null)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, HandleCookies = true,
                BaseAddress = new Uri("https://localhost:7286")
            });
            if (authenticated)
            {
                var ticket = new AuthenticationTicket(principal ?? User(), new AuthenticationProperties
                {
                    IssuedUtc = DateTimeOffset.UtcNow.AddHours(-2),
                    ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddHours(2)
                }, CookieScheme);
                var cookie = CookieOptions.TicketDataFormat.Protect(ticket);
                client.DefaultRequestHeaders.Add("Cookie", CookieName + "=" + cookie);
            }
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environment);
            builder.UseSetting("WorkMonitor:Web:AuthenticationMode", "LocalDevelopment");
            builder.UseSetting("WorkMonitor:Web:DataDirectory", DataDirectory);
            builder.UseSetting("urls", "https://localhost:7286;http://localhost:5191");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkMonitor:Web:AuthenticationMode"] = "LocalDevelopment",
                    ["WorkMonitor:Web:DataDirectory"] = DataDirectory,
                    ["WorkMonitor:Authentication:TenantId"] = Tenant,
                    ["WorkMonitor:Authentication:LoginHint"] = "alice@example.com",
                    ["WorkMonitor:DataDirectory"] = DataDirectory,
                    ["urls"] = "https://localhost:7286;http://localhost:5191",
                    ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                    ["AzureAd:TenantId"] = Tenant,
                    ["AzureAd:ClientId"] = "33333333-3333-3333-3333-333333333333",
                    ["AzureAd:ClientSecret"] = null,
                    ["AzureOpenAI:Endpoint"] = "https://unused-openai.example/",
                    ["AzureOpenAI:DeploymentName"] = "unused-test-deployment",
                    ["AzureOpenAI:ApiKey"] = "test-only-unused-api-key"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILocalDevelopmentIdentity>();
                services.AddSingleton<ILocalDevelopmentIdentity>(Identity);
                services.AddTransient<IStartupFilter, RawConnectionFilter>();
                services.PostConfigure<OpenIdConnectOptions>(OidcScheme, options =>
                {
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                            AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize"
                        });
                    options.Backchannel = new HttpClient(new NoNetworkHandler());
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            _directory.Dispose();
        }
    }

    private sealed class RawConnectionFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, continuation) =>
                {
                    var remote = context.Request.Headers["X-Test-Raw-Remote"].ToString();
                    context.Connection.RemoteIpAddress = remote switch
                    {
                        "none" => null,
                        "" => IPAddress.Loopback,
                        _ => IPAddress.Parse(remote)
                    };
                    var host = context.Request.Headers["X-Test-Raw-Host"].ToString();
                    if (host.Length > 0)
                        context.Request.Host = host == "none" ? new HostString() : new HostString(host);
                    await continuation();
                });
                next(app);
            };
    }

    private sealed class FakeIdentity : ILocalDevelopmentIdentity
    {
        public VerifiedDevOpsIdentity Identity { get; set; } = LocalDevelopmentAuthenticationTests.Identity();
        public Exception? Failure { get; set; }
        public int IdentityCalls { get; private set; }
        public int TokenCalls { get; private set; }
        public ClaimsPrincipal? LastUser { get; private set; }
        public CancellationToken LastCancellation { get; private set; }

        public Task<VerifiedDevOpsIdentity> GetIdentityAsync(CancellationToken cancellationToken)
        {
            IdentityCalls++;
            LastCancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null
                ? Task.FromResult(Identity)
                : Task.FromException<VerifiedDevOpsIdentity>(Failure);
        }

        public Task<string> GetTokenForUserAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            TokenCalls++;
            LastUser = user;
            LastCancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (WebConversationIdentity.GetOwnerId(user) != Identity.OwnerId)
                throw new WebAuthenticationException(403, "local_developer_mismatch", "Wrong developer owner.");
            return Task.FromResult("opaque-developer-resource-token");
        }
    }

    private sealed class DeveloperIdentityFixture : IDisposable
    {
        public CachedTokenCredential Credential { get; } = new();
        public IdentityHandler Handler { get; } = new();
        private readonly HttpClient _http;
        public LocalDevelopmentIdentity Identity { get; }
        public DeveloperIdentityFixture()
        {
            _http = new HttpClient(Handler);
            Identity = new LocalDevelopmentIdentity(new DevOpsTokenService(Credential),
                new WebDevOpsIdentityVerifier(_http, NullLogger.Instance),
                NullLogger<LocalDevelopmentIdentity>.Instance);
        }
        public void Dispose() => _http.Dispose();
    }

    private sealed class CachedTokenCredential : TokenCredential
    {
        public string Token { get; set; } = "opaque-developer-resource-token";
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public CancellationToken LastCancellation { get; private set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => GetTokenAsync(context, cancellationToken).GetAwaiter().GetResult();
        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext context, CancellationToken cancellationToken)
        {
            Calls++;
            LastCancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
                throw Failure;
            return ValueTask.FromResult(new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class IdentityHandler : HttpMessageHandler
    {
        public VerifiedDevOpsIdentity Owner { get; set; } = Identity();
        public int Calls { get; private set; }
        public List<CancellationToken> Cancellations { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Cancellations.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            object body = Calls % 2 == 1
                ? new { authenticatedUser = new { subjectDescriptor = "aad.local-developer" } }
                : new { origin = "aad", domain = Owner.TenantId.ToString(), originId = Owner.ObjectId.ToString() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Local authentication tests must not contact identity providers.");
    }
}
