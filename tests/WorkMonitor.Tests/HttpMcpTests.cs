using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkMonitor.Core.Services;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Mcp.Http;
using HttpProgram = WorkMonitor.Mcp.Http.Program;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace WorkMonitor.Tests;

public sealed class HttpMcpTests
{
    internal const string Tenant = "11111111-2222-3333-4444-555555555555";
    internal const string ClientId = "22222222-3333-4444-5555-666666666666";
    internal const string Alice = "33333333-4444-5555-6666-777777777777";
    internal const string Bob = "44444444-5555-6666-7777-888888888888";
    internal const string Sentinel = "DO-NOT-EXPOSE-TOKEN-CREDENTIAL-OR-TICKET";
    internal const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    [Fact]
    public async Task ActualHttpTransportInitializesDiscoversAndLooksUpWithoutModelOrDesktopIdentity()
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        await using var mcp = await Connect(http);
        var tools = await mcp.ListToolsAsync();
        Assert.Equal(new[] { "analyze_feedback_themes", "get_work_item", "list_project_areas", "list_saved_queries" },
            tools.Select(tool => tool.Name).OrderBy(name => name));
        Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.Equal("summary", tools.Single(tool => tool.Name == "get_work_item").JsonSchema
            .GetProperty("properties").GetProperty("view").GetProperty("default").GetString());
        var queries = await mcp.CallToolAsync("list_saved_queries", new Dictionary<string, object?>());
        Assert.NotEqual(true, queries.IsError);
        Assert.Equal("FTMonitor - FTS", Read(queries).GetProperty("queries")[0].GetProperty("name").GetString());
        var areas = await mcp.CallToolAsync("list_project_areas", new Dictionary<string, object?>());
        Assert.NotEqual(true, areas.IsError);
        Assert.Empty(app.Obo.Assertions);
        var result = Read(await mcp.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 }));
        AssertTicket(result, Alice);
        Assert.False(result.TryGetProperty("description", out _));
        Assert.Equal("summary", result.GetProperty("coverage").GetProperty("view").GetString());
        Assert.False(result.GetProperty("coverage").GetProperty("isPartial").GetBoolean());
        Assert.Single(app.Source.Requests);
        Assert.DoesNotContain("System.Description", app.Source.Requests.Single().Fields);
        Assert.Equal(http.DefaultRequestHeaders.Authorization!.Parameter, Assert.Single(app.Obo.Assertions));
        Assert.Equal("downstream-" + Alice, app.Source.Requests.Single().Token);
        Assert.Null(mcp.SessionId);
        Assert.Null(app.Services.GetService<TokenCredential>());
        Assert.Null(app.Services.GetService<DevOpsTokenService>());
    }

    [Fact]
    public async Task ActualHttpTransportAndProductionMsalKeepTwoUsersAssertionsAndTokensSeparate()
    {
        await using var app = new HttpMcpFactory(realObo: true);
        app.IdentitySource.TokenForAssertion = assertion => "downstream-" +
            new JwtSecurityTokenHandler().ReadJwtToken(assertion).Claims.Single(claim => claim.Type == "oid").Value;
        using var aliceHttp = app.UserClient(Alice);
        using var bobHttp = app.UserClient(Bob);
        await using var alice = await Connect(aliceHttp);
        await using var bob = await Connect(bobHttp);
        var responses = await Task.WhenAll(
            alice.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 }).AsTask(),
            bob.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 }).AsTask());
        AssertTicket(Read(responses[0]), Alice);
        AssertTicket(Read(responses[1]), Bob);
        Assert.Equal(2, app.IdentitySource.TokenRequests.Count);
        Assert.Equal(new[] { aliceHttp.DefaultRequestHeaders.Authorization!.Parameter, bobHttp.DefaultRequestHeaders.Authorization!.Parameter }
            .OrderBy(value => value), app.IdentitySource.TokenRequests.Select(request => request["assertion"]).OrderBy(value => value));
        Assert.All(app.IdentitySource.TokenRequests, request => Assert.Equal("on_behalf_of", request["requested_token_use"]));
        Assert.DoesNotContain(Sentinel, string.Join("\n", app.Logs));
    }

    [Fact]
    public async Task ExplicitDetailReusesBoundedStageOneContract()
    {
        await using var app = new HttpMcpFactory();
        app.Source.Description = new string('<', 10000);
        using var http = app.UserClient();
        await using var mcp = await Connect(http);
        var result = Read(await mcp.CallToolAsync("get_work_item",
            new Dictionary<string, object?> { ["id"] = 42, ["view"] = "detail" }));
        AssertTicket(result, Alice);
        Assert.Equal("Engineer", result.GetProperty("assignedTo").GetProperty("displayName").GetString());
        Assert.Equal(2, app.Source.Requests.Count);
        Assert.Contains("$top=20&order=desc", app.Source.Requests.Last().Path);
        var coverage = result.GetProperty("coverage");
        Assert.Equal("detail", coverage.GetProperty("view").GetString());
        Assert.True(coverage.GetProperty("isPartial").GetBoolean());
        Assert.True(coverage.GetProperty("outputTruncated").GetBoolean());
        Assert.Contains("description", coverage.GetProperty("truncatedFields").EnumerateArray().Select(value => value.GetString()));
        Assert.InRange(result.GetRawText().Length, 1, 14000);
        Assert.False(result.TryGetProperty("preview", out _));
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("no-exp", HttpStatusCode.Unauthorized)]
    [InlineData("future", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-algorithm", HttpStatusCode.Unauthorized)]
    [InlineData("unsigned", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-audience", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-issuer", HttpStatusCode.Unauthorized)]
    [InlineData("wrong-tenant", HttpStatusCode.Forbidden)]
    [InlineData("app-only", HttpStatusCode.Forbidden)]
    [InlineData("app-with-scope", HttpStatusCode.Forbidden)]
    [InlineData("wrong-scope", HttpStatusCode.Forbidden)]
    [InlineData("substring-scope", HttpStatusCode.Forbidden)]
    [InlineData("missing-oid", HttpStatusCode.Forbidden)]
    [InlineData("invalid-oid", HttpStatusCode.Forbidden)]
    [InlineData("duplicate-oid", HttpStatusCode.Forbidden)]
    [InlineData("missing-tenant", HttpStatusCode.Forbidden)]
    [InlineData("wrong-version", HttpStatusCode.Forbidden)]
    public async Task EveryMcpRequestRequiresValidSingleTenantDelegatedJwt(string variant, HttpStatusCode status)
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient(tokenVariant: variant);
        foreach (var method in new[] { "initialize", "tools/list", "tools/call" })
        {
            using var response = await Post(http, method, method == "tools/call" ? new { name = "get_work_item", arguments = new { id = 42 } } : new { });
            Assert.Equal(status, response.StatusCode);
            Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    [Fact]
    public async Task InitialAuthenticationDoesNotAuthorizeLaterRequests()
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        await using var mcp = await Connect(http);
        http.DefaultRequestHeaders.Authorization = null;
        using var response = await Post(http, "tools/list", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Obo.Assertions);
    }

    [Fact]
    public async Task FinalStreamableHttpHeadersProhibitTicketResponseCaching()
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        using var response = await Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        Assert.Contains("Ticket for " + Alice, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("[]", HttpStatusCode.BadRequest)]
    [InlineData("not json", HttpStatusCode.BadRequest)]
    [InlineData("oversized", HttpStatusCode.RequestEntityTooLarge)]
    public async Task InvalidOrOversizedRequestsDoNotAcquireTokens(string body, HttpStatusCode status)
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body == "oversized" ? "{\"oversized\":\"" + new string('x', 65536) + "\"}" : body,
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await http.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    [Theory]
    [InlineData("add_work_item_comment")]
    [InlineData("update_work_item")]
    [InlineData("create_bug_from_feedback")]
    [InlineData("execute_saved_query")]
    [InlineData("assess_bug_probability")]
    [InlineData("unknown")]
    public async Task ManualDisallowedCallsAreBlockedBeforeOboEvenWithWritesEnabledAndConfirmation(string name)
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        await using var mcp = await Connect(http);
        using var response = await Post(http, "tools/call", new
        {
            name, arguments = new { id = 42, confirm = true, dryRun = false, text = "Must not run" }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(-32602, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(9, error.GetProperty("id").GetInt32());
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
        Assert.Empty(app.ModelSource.Requests);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"id":0}""")]
    [InlineData("""{"id":"42"}""")]
    [InlineData("""{"id":42,"view":"all"}""")]
    [InlineData("""{"id":42,"view":null}""")]
    [InlineData("""{"id":42,"accessToken":"override"}""")]
    [InlineData("""{"id":42,"confirm":true}""")]
    public async Task InvalidLookupArgumentsFailBeforeObo(string arguments)
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        using var response = await Post(http, "tools/call",
            new { name = "get_work_item", arguments = JsonSerializer.Deserialize<JsonElement>(arguments) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(-32602, json.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(app.Obo.Assertions);
    }

    [Fact]
    public async Task RouteTrailingSlashStillPerformsOboAndClaimsChallenges()
    {
        await using var app = new HttpMcpFactory();
        app.Obo.Failure = new MsalUiRequiredException("invalid_grant", Sentinel);
        using var http = app.UserClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "get_work_item", arguments = new { id = 42 } } })
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(app.Obo.Assertions);
        Assert.NotEmpty(response.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task InterleavedAndConcurrentUsersNeverShareAssertionTokenOrTicketResult()
    {
        await using var app = new HttpMcpFactory();
        app.Obo.Interleave = true;
        using var aliceHttp = app.UserClient(Alice);
        using var bobHttp = app.UserClient(Bob);
        await using var alice = await Connect(aliceHttp);
        await using var bob = await Connect(bobHttp);
        for (var index = 0; index < 3; index++)
        {
            var results = await Task.WhenAll(
                alice.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 }).AsTask(),
                bob.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 }).AsTask());
            AssertTicket(Read(results[0]), Alice);
            AssertTicket(Read(results[1]), Bob);
        }
        Assert.Equal(6, app.Obo.Assertions.Count);
        Assert.Equal(6, app.Source.Requests.Count);
        Assert.Equal(3, app.Source.Requests.Count(request => request.Token == "downstream-" + Alice));
        Assert.Equal(3, app.Source.Requests.Count(request => request.Token == "downstream-" + Bob));
        Assert.DoesNotContain(app.Source.Requests, request => app.Obo.Assertions.Contains(request.Token));

        bobHttp.DefaultRequestHeaders.Add("Mcp-Session-Id", "a-session-supplied-by-a-different-user");
        using var response = await Post(bobHttp, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        Assert.Contains("sessions_not_supported", await response.Content.ReadAsStringAsync());
        Assert.Equal(6, app.Source.Requests.Count);
        Assert.Equal(6, app.Obo.Assertions.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("malformed")]
    public async Task BadSourcesFailWithoutTicketBodiesOrNullSuccess(string mode)
    {
        await using var app = new HttpMcpFactory();
        app.Source.Mode = mode;
        using var http = app.UserClient();
        await using var mcp = await Connect(http);
        var response = await mcp.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 });
        Assert.True(response.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text;
        Assert.DoesNotContain(Sentinel, text);
        Assert.Contains(mode switch { "missing" => "not found", "denied" => "denied", _ => "valid bounded data" }, text);
        Assert.Single(app.Source.Requests);
        Assert.DoesNotContain(Sentinel, string.Join("\n", app.Logs));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("null")]
    [InlineData("\"42\"")]
    public async Task ContradictoryOrMalformedDiscussionOwnershipNeverReachesHttpClient(string owner)
    {
        await using var app = new HttpMcpFactory();
        app.Source.CommentJson = $$"""{"totalCount":1,"comments":[{"workItemId":{{owner}},"text":"{{Sentinel}}"}]}""";
        using var http = app.UserClient();
        await using var mcp = await Connect(http);
        var response = await mcp.CallToolAsync("get_work_item",
            new Dictionary<string, object?> { ["id"] = 42, ["view"] = "detail" });
        Assert.True(response.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text;
        Assert.Contains("valid bounded data", text);
        Assert.DoesNotContain(Sentinel, text + string.Join("\n", app.Logs));
        Assert.Equal(2, app.Source.Requests.Count);
    }

    [Fact]
    public async Task OboInteractionIsHttp401WithUsableClaimsChallengeBeforeMcpStreaming()
    {
        await using var app = new HttpMcpFactory(realObo: true);
        const string claims = """{"access_token":{"acrs":{"essential":true,"value":"cp1"}}}""";
        app.IdentitySource.Claims = claims;
        app.IdentitySource.Mode = "interaction";
        using var http = app.UserClient();
        using var response = await Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate).ToString();
        Assert.Contains("error=\"insufficient_claims\"", challenge);
        Assert.Contains($"authorization_uri=\"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize\"", challenge);
        Assert.Contains($"claims=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(claims))}\"", challenge);
        Assert.Contains("devops_interaction_required", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        Assert.Empty(app.Source.Requests);
        Assert.DoesNotContain(Sentinel, challenge + await response.Content.ReadAsStringAsync() + string.Join("\n", app.Logs));
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("credential")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("empty")]
    [InlineData("passthrough")]
    public async Task OboErrorsAreActionableSanitizedAndNeverFallback(string failure)
    {
        await using var app = new HttpMcpFactory();
        app.Obo.Failure = failure switch
        {
            "consent" => new MsalUiRequiredException("invalid_grant", Sentinel),
            "credential" => new MsalServiceException("invalid_client", Sentinel),
            "network" => new HttpRequestException(Sentinel),
            "timeout" => new TaskCanceledException(Sentinel),
            _ => null
        };
        app.Obo.ResultMode = failure;
        using var http = app.UserClient();
        using var response = await Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } });
        Assert.Equal(failure == "consent" ? HttpStatusCode.Unauthorized :
            failure == "timeout" ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Sentinel, content + string.Join("\n", app.Logs));
        Assert.DoesNotContain(http.DefaultRequestHeaders.Authorization!.Parameter!, content);
        Assert.Empty(app.Source.Requests);
        Assert.Single(app.Obo.Assertions);
    }

    [Fact]
    public async Task HttpCancellationReachesOboAndPreventsDownstreamWork()
    {
        await using var app = new HttpMcpFactory();
        app.Obo.Block = true;
        using var http = app.UserClient();
        using var cancellation = new CancellationTokenSource();
        var pending = Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } }, cancellation.Token);
        await app.Obo.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await app.Obo.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(app.Source.Requests);
    }

    [Fact]
    public async Task HttpCancellationAlsoReachesDownstreamWorkItemRead()
    {
        await using var app = new HttpMcpFactory();
        app.Source.Block = true;
        using var http = app.UserClient();
        using var cancellation = new CancellationTokenSource();
        var pending = Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } }, cancellation.Token);
        await app.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await app.Source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(app.Obo.Assertions);
        Assert.Single(app.Source.Requests);
    }

    [Theory]
    [InlineData("TenantId", "")]
    [InlineData("ClientId", "not-an-app-id")]
    [InlineData("ClientSecret", "")]
    public async Task InvalidAuthenticationSettingsFailActualHostStartup(string name, string value)
    {
        await using var app = new HttpMcpFactory();
        app.ConfigurationOverrides[$"WorkMonitor:HttpMcp:{name}"] = value;
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.DoesNotContain(Sentinel, error.ToString());
    }

    [Fact]
    public async Task IncomingDevOpsOverrideIsRejectedAndNoUiIsServed()
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        foreach (var path in new[] { "/", "/feedback-dashboard.html", "/agent.html", "/api/work-item-details/42" })
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);
        http.DefaultRequestHeaders.Add("X-WorkMonitor-DevOps-Token", Sentinel);
        using var response = await Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("token_override_not_supported", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    [Fact]
    public async Task PlainHttpIsRejectedInsteadOfRedirectingOrReadingDevOps()
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        http.BaseAddress = new Uri("http://localhost");
        using var response = await Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Empty(app.Obo.Assertions);
    }

    [Theory]
    [InlineData("attacker.invalid")]
    [InlineData("localhost.attacker.invalid")]
    [InlineData("127.0.0.2")]
    [InlineData("[::2]")]
    public async Task SpoofedHostIsRejectedBeforeOboEvenWithValidUserToken(string host)
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        http.DefaultRequestHeaders.Host = host;
        http.DefaultRequestHeaders.Add("X-Forwarded-Host", "localhost");
        foreach (var method in new[] { "initialize", "tools/list", "tools/call" })
        {
            using var response = await Post(http, method,
                method == "tools/call" ? new { name = "get_work_item", arguments = new { id = 42 } } : new { });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(host, await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    [Theory]
    [InlineData("localhost:7443")]
    [InlineData("127.0.0.1:7443")]
    [InlineData("[::1]:7443")]
    public async Task DefaultLoopbackHostsAllowNoOriginServiceClients(string host)
    {
        await using var app = new HttpMcpFactory();
        app.ConfigurationOverrides["AllowedHosts"] = null;
        using var http = app.UserClient();
        http.DefaultRequestHeaders.Host = host;
        await using var mcp = await Connect(http);
        AssertTicket(Read(await mcp.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 })), Alice);
        Assert.Single(app.Obo.Assertions);
        Assert.Single(app.Source.Requests);
    }

    [Fact]
    public async Task ConfiguredProductionHostAllowsOnlyThatExactName()
    {
        await using var app = new HttpMcpFactory();
        app.ConfigurationOverrides["AllowedHosts"] = "mcp.example.invalid";
        using var http = app.UserClient();
        http.BaseAddress = new Uri("https://mcp.example.invalid:7443");
        await using var mcp = await Connect(http);
        Assert.Equal(4, (await mcp.ListToolsAsync()).Count);
        AssertTicket(Read(await mcp.CallToolAsync("get_work_item", new Dictionary<string, object?> { ["id"] = 42 })), Alice);
        foreach (var rejected in new[] { "localhost", "mcp.example.invalid.attacker.invalid", "sub.mcp.example.invalid" })
        {
            http.DefaultRequestHeaders.Host = rejected;
            using var response = await Post(http, "tools/call", new { name = "get_work_item", arguments = new { id = 42 } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Single(app.Obo.Assertions);
        Assert.Single(app.Source.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not an origin")]
    [InlineData("https://foreign.example.invalid")]
    [InlineData("https://localhost")]
    public async Task AnyBrowserOriginIsRejectedBeforeOboEvenWithValidUserToken(string origin)
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        Assert.True(http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin));
        foreach (var method in new[] { "initialize", "tools/list", "tools/call" })
        {
            using var response = await Post(http, method,
                method == "tools/call" ? new { name = "get_work_item", arguments = new { id = 42 } } : new { });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("browser_origin_not_supported", await response.Content.ReadAsStringAsync());
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    [Theory]
    [InlineData("Origin", 403)]
    [InlineData("Host", 400)]
    public async Task EmptyBoundaryHeaderIsRejectedBeforeOboEvenWithValidUserToken(string header, int status)
    {
        await using var app = new HttpMcpFactory();
        using var http = app.UserClient();
        // HttpClient drops empty header values; send the header through TestServer directly.
        var response = await app.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Host = new("localhost");
            context.Request.Path = "/mcp";
            context.Request.Headers.Authorization = http.DefaultRequestHeaders.Authorization!.ToString();
            context.Request.Headers[header] = "";
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0", id = 9, method = "tools/call",
                @params = new { name = "get_work_item", arguments = new { id = 42 } }
            }));
        });
        Assert.Equal(status, response.Response.StatusCode);
        Assert.False(response.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.example.invalid")]
    [InlineData("+")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("localhost;*")]
    [InlineData("localhost;;mcp.example.invalid")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("[0:0:0:0:0:0:0:0]")]
    [InlineData("[0:0:0:0:0:0:0:1]")]
    [InlineData("::1")]
    [InlineData("https://mcp.example.invalid")]
    [InlineData("mcp.example.invalid:7443")]
    public async Task UnrestrictedOrMalformedAllowedHostsFailActualStartup(string hosts)
    {
        await using var app = new HttpMcpFactory();
        app.ConfigurationOverrides["AllowedHosts"] = hosts;
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("AllowedHosts", error.Message);
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    [Fact]
    public async Task AllowedHostsArrayConfigurationFailsActualStartup()
    {
        await using var app = new HttpMcpFactory();
        app.ConfigurationOverrides["AllowedHosts:0"] = "*";
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("AllowedHosts", error.Message);
        Assert.Empty(app.Obo.Assertions);
        Assert.Empty(app.Source.Requests);
    }

    internal static async Task<McpClient> Connect(HttpClient http)
        => await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        }, http), new McpClientOptions { ProtocolVersion = "2025-11-25" });

    internal static JsonElement Read(CallToolResult response)
    {
        Assert.NotEqual(true, response.IsError);
        return JsonSerializer.Deserialize<JsonElement>(Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
    }

    private static void AssertTicket(JsonElement result, string user)
    {
        Assert.Equal(42, result.GetProperty("id").GetInt32());
        Assert.Equal(7, result.GetProperty("revision").GetInt32());
        Assert.Equal("Ticket for " + user, result.GetProperty("title").GetString());
        Assert.Equal("Active", result.GetProperty("state").GetString());
        Assert.Equal(2, result.GetProperty("priority").GetInt32());
        Assert.Equal("https://dev.azure.com/devdiv/devdiv/_workitems/edit/42", result.GetProperty("url").GetString());
    }

    internal static async Task<HttpResponseMessage> Post(HttpClient client, string method, object parameters, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 9, method, @params = parameters })
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        return await client.SendAsync(request, cancellationToken);
    }
}

internal sealed class HttpMcpFactory(bool realObo = false, bool productionHttpHandlers = false) : WebApplicationFactory<HttpProgram>
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workmonitor-http-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _originalAreas = Constants.ProjectAreaPath;
    public FakeOboAcquirer Obo { get; } = new();
    public HttpTicketSource Source { get; } = new();
    public MsalIdentitySource IdentitySource { get; } = new();
    public HttpThemeSource ThemeSource { get; } = new();
    public HttpThemeModelSource ModelSource { get; } = new();
    public bool UseThemeSource { get; set; }
    public bool SeedCatalog { get; set; }
    public ConcurrentQueue<string> Logs { get; } = new();
    public Dictionary<string, string?> ConfigurationOverrides { get; } = new();

    public HttpClient UserClient(string user = HttpMcpTests.Alice, string tokenVariant = "valid")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        if (tokenVariant != "anonymous")
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user, tokenVariant));
        return client;
    }

    private string Token(string user, string variant)
    {
        var claims = new List<Claim>
        {
            new("tid", variant == "wrong-tenant" ? HttpMcpTests.Bob : HttpMcpTests.Tenant),
            new("oid", user), new("ver", variant == "wrong-version" ? "1.0" : "2.0"),
            new("scp", variant is "wrong-scope" ? "other" : variant == "substring-scope" ? "not_access_as_user" : "access_as_user")
        };
        if (variant == "app-only") { claims.RemoveAll(claim => claim.Type == "scp"); claims.Add(new("roles", "access_as_user")); }
        if (variant == "app-with-scope") claims.Add(new("idtyp", "app"));
        if (variant is "missing-oid" or "invalid-oid") claims.RemoveAll(claim => claim.Type == "oid");
        if (variant == "invalid-oid") claims.Add(new("oid", "not-a-guid"));
        if (variant == "duplicate-oid") claims.Add(new("oid", HttpMcpTests.Bob));
        if (variant == "missing-tenant") claims.RemoveAll(claim => claim.Type == "tid");
        using var wrongKey = RSA.Create(2048);
        var token = new JwtSecurityToken(
            variant == "wrong-issuer" ? "https://untrusted.invalid/" : HttpMcpTests.Issuer,
            variant == "wrong-audience" ? "another-api" : HttpMcpTests.ClientId, claims,
            DateTime.UtcNow.AddMinutes(variant == "future" ? 5 : -10),
            variant == "no-exp" ? null : DateTime.UtcNow.AddMinutes(variant == "expired" ? -1 : 15),
            variant == "unsigned" ? null : new SigningCredentials(
                new RsaSecurityKey(variant == "wrong-key" ? wrongKey : _rsa) { KeyId = "fixture" },
                variant == "wrong-algorithm" ? SecurityAlgorithms.RsaSha384 : SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (SeedCatalog)
        {
            Directory.CreateDirectory(Path.Combine(_directory, "data"));
            File.WriteAllText(Path.Combine(_directory, "data", "parameters.json"), """{"Pilot":"Example\\Feedback"}""");
            File.WriteAllText(Path.Combine(_directory, "data", "saved-queries.json"),
                $$"""{"FTMonitor - FTS":"{{HttpThemeTests.QueryId}}"}""");
        }
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AllowedHosts"] = "localhost;127.0.0.1;[::1]",
                ["WorkMonitor:HttpMcp:TenantId"] = HttpMcpTests.Tenant,
                ["WorkMonitor:HttpMcp:ClientId"] = HttpMcpTests.ClientId,
                ["WorkMonitor:HttpMcp:RequiredScope"] = "access_as_user",
                ["WorkMonitor:HttpMcp:ClientSecret"] = "unused-fixture-credential",
                ["WorkMonitor:HttpMcp:DataDirectory"] = _directory,
                ["WorkMonitor:Mcp:AllowWrites"] = "true",
                ["AzureOpenAI:Endpoint"] = "", ["AzureOpenAI:DeploymentName"] = "", ["AzureOpenAI:ApiKey"] = "",
                ["AzureDevOps:SavedQueries:0:name"] = "FTMonitor - FTS",
                ["AzureDevOps:SavedQueries:0:queryId"] = "55555555-6666-7777-8888-999999999999"
            }).AddInMemoryCollection(ConfigurationOverrides));
        builder.ConfigureLogging(logging => logging.AddProvider(new RecordingLoggerProvider(Logs)));
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = HttpMcpTests.Issuer };
                configuration.SigningKeys.Add(new RsaSecurityKey(_rsa) { KeyId = "fixture" });
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
            if (!realObo)
            {
                services.RemoveAll<IDevOpsOboTokenAcquirer>();
                services.AddSingleton<IDevOpsOboTokenAcquirer>(Obo);
            }
            if (!productionHttpHandlers)
            {
                services.AddHttpClient("EntraObo").ConfigurePrimaryHttpMessageHandler(() => IdentitySource);
                services.AddHttpClient("DevOps").ConfigurePrimaryHttpMessageHandler(() => UseThemeSource ? ThemeSource : Source);
                services.AddHttpClient("ThemeModel").ConfigurePrimaryHttpMessageHandler(() => ModelSource);
            }
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _rsa.Dispose();
        if (SeedCatalog) Constants.ProjectAreaPath = _originalAreas;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(messages);
        public void Dispose() { }
        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
        }
    }
}

internal sealed class FakeOboAcquirer : IDevOpsOboTokenAcquirer
{
    public ConcurrentQueue<string> Assertions { get; } = new();
    public Exception? Failure { get; set; }
    public string? ResultMode { get; set; }
    public bool Interleave { get; set; }
    public bool Block { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> AcquireAsync(string validatedUserAssertion, CancellationToken cancellationToken)
    {
        Assertions.Enqueue(validatedUserAssertion);
        Entered.TrySetResult();
        if (Failure != null) throw Failure;
        try
        {
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Interleave) await Task.Delay(30, cancellationToken);
        }
        catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        return ResultMode switch
        {
            "empty" => "",
            "passthrough" => validatedUserAssertion,
            _ => "downstream-" + new JwtSecurityTokenHandler().ReadJwtToken(validatedUserAssertion).Claims.Single(claim => claim.Type == "oid").Value
        };
    }
}

internal sealed class HttpTicketSource : HttpMessageHandler
{
    public ConcurrentQueue<(string Path, string Token, string[] Fields)> Requests { get; } = new();
    public string? Mode { get; set; }
    public string Description { get; set; } = "Ticket details";
    public string? CommentJson { get; set; }
    public bool Block { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization?.Parameter ?? "";
        var fields = Array.Empty<string>();
        if (request.Content != null)
        {
            var body = await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            fields = body.GetProperty("fields").EnumerateArray().Select(field => field.GetString()!).ToArray();
        }
        Requests.Enqueue((request.RequestUri!.PathAndQuery, token, fields));
        Entered.TrySetResult();
        if (Block)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        Assert.StartsWith("downstream-", token);
        var user = token["downstream-".Length..];
        var json = request.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.OrdinalIgnoreCase)
            ? CommentJson ?? """{"comments":[],"totalCount":0}"""
            : JsonSerializer.Serialize(new
            {
                value = new[] { new
                {
                    id = 42, rev = 7, fields = new Dictionary<string, object>
                    {
                        ["System.Title"] = "Ticket for " + user, ["System.State"] = "Active",
                        ["Microsoft.VSTS.Common.Priority"] = 2,
                        ["System.AssignedTo"] = new { displayName = "Engineer", uniqueName = "fixture@example.invalid" },
                        ["System.Description"] = Description
                    }
                }}
            });
        json = Mode switch
        {
            "missing" => """{"value":[]}""",
            "malformed" => JsonSerializer.Serialize(new { value = new[] { new { id = 99, rev = 7,
                fields = new Dictionary<string, string> { ["System.Title"] = HttpMcpTests.Sentinel } } } }),
            "denied" => HttpMcpTests.Sentinel,
            _ => json
        };
        return new HttpResponseMessage(Mode == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
