using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FT_MONITOR.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Conversations;

namespace WorkMonitor.Tests;

public sealed class WebConversationTests
{
    private const string Alice = "11111111-1111-1111-1111-111111111111";
    private const string Bob = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task AnonymousRequestsAreRejected()
    {
        await using var app = new ChatFactory();
        using var client = app.CreateClient();

        var result = await client.GetAsync("/api/agent/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task CreatingConversationWithDelegatedApiIdentityDoesNotRequireAntiforgeryToken()
    {
        await using var app = new ChatFactory();
        using var client = app.UserClient(Alice);

        var result = await client.PostAsJsonAsync("/api/agent/conversations", new { });

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Empty(app.Model.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("user_impersonation")]
    [InlineData("access_as_user_extra")]
    public async Task MissingOrWrongDelegatedApiScopeCannotReadCreateOrStream(string scope)
    {
        await using var app = new ChatFactory();
        using var authorized = app.UserClient(Alice);
        var id = await CreateAsync(authorized);
        using var client = app.UserClient(Alice, scope);

        using var read = await client.GetAsync("/api/agent/conversations");
        using var create = await client.PostAsJsonAsync("/api/agent/conversations", new { });
        using var stream = await client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream", new { prompt = "Unauthorized prompt" });

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
        Assert.Empty(app.Model.Requests);
        Assert.Empty(app.Tokens.RequestedUsers);
        var saved = await authorized.GetFromJsonAsync<JsonElement>($"/api/agent/conversations/{id}");
        Assert.Empty(saved.GetProperty("messages").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task OwnersCannotReadOrWriteAnotherUsersConversation()
    {
        await using var app = new ChatFactory();
        using var alice = app.UserClient(Alice);
        using var bob = app.UserClient(Bob);
        var id = await CreateAsync(alice);

        var read = await bob.GetAsync($"/api/agent/conversations/{id}");
        var write = await bob.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream", new { prompt = "Cross-user request" });
        var list = await bob.GetFromJsonAsync<JsonElement>("/api/agent/conversations");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Empty(list.EnumerateArray().ToArray());
        Assert.Empty(app.Model.Requests);
    }

    [Fact]
    public async Task StreamingPersistsHistoryAndSuppliesItToFollowUp()
    {
        await using var app = new ChatFactory();
        using var client = app.UserClient(Alice);
        var id = await CreateAsync(client);

        var first = await client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream", new { prompt = "Remember my first question" });
        var firstEvents = await first.Content.ReadAsStringAsync();
        var second = await client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream", new { prompt = "Follow up on it" });
        var secondEvents = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/x-ndjson", first.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"kind\":\"textDelta\"", firstEvents);
        Assert.Contains("\"kind\":\"completed\"", secondEvents);
        var requests = app.Model.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Contains(requests[1], message =>
            message.Role == AgentModelMessageRole.User &&
            message.Content == "Remember my first question");
        Assert.Contains(requests[1], message =>
            message.Role == AgentModelMessageRole.Assistant &&
            message.Content == "Answer: Remember my first question");
        Assert.Equal(new[] { Alice, Alice }, app.Tokens.RequestedUsers.ToArray());

        var saved = await client.GetFromJsonAsync<JsonElement>(
            $"/api/agent/conversations/{id}");
        Assert.Equal(4, saved.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task LiveWriteModeIsRejectedByServer()
    {
        await using var app = new ChatFactory();
        using var client = app.UserClient(Alice);
        var id = await CreateAsync(client);

        var result = await client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream",
            new { prompt = "Write a comment", dryRun = false });

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Empty(app.Model.Requests);
        var tools = await client.GetFromJsonAsync<JsonElement>("/api/agent/tools");
        Assert.All(tools.EnumerateArray(), tool =>
            Assert.False(tool.GetProperty("isWriteTool").GetBoolean()));
        Assert.Equal(12, tools.GetArrayLength());
        Assert.DoesNotContain(tools.EnumerateArray(), tool =>
            tool.GetProperty("name").GetString() == "update_work_item");
        Assert.DoesNotContain(tools.EnumerateArray(), tool =>
            tool.GetProperty("name").GetString() == "create_bug_from_feedback");
    }

    [Fact]
    public async Task DelegatedAuthFailureIsActionableAndDoesNotSavePrompt()
    {
        await using var app = new ChatFactory();
        app.Tokens.Reject = true;
        using var client = app.UserClient(Alice);
        var id = await CreateAsync(client);

        var result = await client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream", new { prompt = "Show my items" });
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        var saved = await client.GetFromJsonAsync<JsonElement>(
            $"/api/agent/conversations/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("consent_required", body.GetProperty("errorCode").GetString());
        Assert.Empty(saved.GetProperty("messages").EnumerateArray().ToArray());
        Assert.Empty(app.Model.Requests);
    }

    [Fact]
    public async Task ConcurrentTurnsInOneConversationAreRejected()
    {
        await using var app = new ChatFactory();
        using var client = app.UserClient(Alice);
        var id = await CreateAsync(client);
        app.Model.BlockNext = true;
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/agent/conversations/{id}/stream")
        {
            Content = JsonContent.Create(new { prompt = "First turn" })
        };
        var firstTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await app.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = await client.PostAsJsonAsync(
                $"/api/agent/conversations/{id}/stream", new { prompt = "Overlapping turn" });
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }
        finally
        {
            app.Model.Release.TrySetResult(true);
        }

        using var first = await firstTask;
        Assert.Contains("\"kind\":\"completed\"", await first.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CompletedToolOutcomesAreSavedAndReplayedToFollowUp()
    {
        await using var app = new ChatFactory();
        using var client = app.UserClient(Alice);
        var id = await CreateAsync(client);

        var first = await client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream", new { prompt = "Use the project areas tool" });
        var events = await first.Content.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"toolCompleted\"", events);

        var second = await client.PostAsJsonAsync(
            $"/api/agent/conversations/{id}/stream", new { prompt = "Follow up on the result" });
        second.EnsureSuccessStatusCode();
        await second.Content.ReadAsStringAsync();

        var saved = await client.GetFromJsonAsync<JsonElement>(
            $"/api/agent/conversations/{id}");
        Assert.Contains(saved.GetProperty("messages").EnumerateArray(), message =>
            message.GetProperty("role").GetString() == "tool");
        Assert.Contains(app.Model.Requests.Last(), message =>
            message.Content.StartsWith("UNTRUSTED TOOL OBSERVATION", StringComparison.Ordinal));
    }

    private static async Task<string> CreateAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/agent/conversations", new { });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task OlderConversationsAreAccessibleThroughOwnerScopedPagination()
    {
        await using var app = new ChatFactory();
        using var alice = app.UserClient(Alice);
        using var bob = app.UserClient(Bob);
        var ids = new HashSet<string>();
        for (var index = 0; index < 21; index++)
            ids.Add(await CreateAsync(alice));
        await CreateAsync(bob);

        var newest = await alice.GetFromJsonAsync<JsonElement>(
            "/api/agent/conversations?offset=0&limit=20");
        var older = await alice.GetFromJsonAsync<JsonElement>(
            "/api/agent/conversations?offset=20&limit=20");

        Assert.Equal(20, newest.GetArrayLength());
        Assert.Equal(1, older.GetArrayLength());
        var listed = newest.EnumerateArray().Concat(older.EnumerateArray())
            .Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet();
        Assert.True(ids.SetEquals(listed));
        var invalid = await alice.GetAsync("/api/agent/conversations?offset=-1");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class ChatFactory : WebApplicationFactory<FT_MONITOR.Program>
    {
        private readonly string _directory = Path.Combine(
            AppContext.BaseDirectory, "web-conversation-test-data", Guid.NewGuid().ToString("N"));

        public RecordingModel Model { get; } = new();
        public UserTokenService Tokens { get; } = new();

        public HttpClient UserClient(string userId, string scope = "access_as_user")
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            client.DefaultRequestHeaders.Add("X-Test-User", userId);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Scope", scope);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("WorkMonitor:Web:DataDirectory", _directory);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureAd:TenantId"] = "72f988bf-86f1-41af-91ab-2d7cd011db47",
                    ["AzureAd:ClientId"] = "33333333-3333-3333-3333-333333333333"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.RemoveAll<IAgentModel>();
                services.AddSingleton<IAgentModel>(Model);
                services.RemoveAll<IWebDevOpsTokenService>();
                services.AddScoped<IWebDevOpsTokenService>(provider =>
                    new UserTokenServiceScope(
                        Tokens, provider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()));
                services.RemoveAll<SessionStore>();
                services.AddSingleton(new SessionStore(Path.Combine(_directory, "conversations.db")));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = Request.Headers["X-Test-User"].ToString();
            if (!Guid.TryParse(id, out _)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("oid", id),
                new Claim("tid", "72f988bf-86f1-41af-91ab-2d7cd011db47"),
                new Claim("scp", Request.Headers["X-Test-Scope"].ToString()),
                new Claim("name", id == Alice ? "Alice" : "Bob"),
                new Claim("preferred_username", id == Alice ? "alice@example.com" : "bob@example.com")
            }, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class UserTokenService
    {
        public bool Reject { get; set; }
        public ConcurrentQueue<string> RequestedUsers { get; } = new();
    }

    private sealed class UserTokenServiceScope(
        UserTokenService state,
        Microsoft.AspNetCore.Http.IHttpContextAccessor accessor) : IWebDevOpsTokenService
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            var id = accessor.HttpContext!.User.FindFirst("oid")!.Value;
            state.RequestedUsers.Enqueue(id);
            if (state.Reject)
                throw new WebAuthenticationException(403, "consent_required", "Grant delegated DevOps consent.");
            return Task.FromResult("test-user-token");
        }
    }

    private sealed class RecordingModel : IAgentModel
    {
        public string ModelName => "test-model";
        public ConcurrentQueue<IReadOnlyList<AgentModelMessage>> Requests { get; } = new();
        public bool BlockNext { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AgentModelUpdate> StreamAsync(
            IReadOnlyList<AgentModelMessage> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(messages.ToArray());
            if (BlockNext)
            {
                BlockNext = false;
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            var prompt = messages.Last(message => message.Role == AgentModelMessageRole.User).Content;
            if (prompt == "Use the project areas tool" &&
                messages.Last().Role != AgentModelMessageRole.Tool)
            {
                yield return new AgentModelUpdate(
                    CompletedResponse: new AgentModelResponse(
                        "",
                        new[] { new AgentToolCall("areas-call", "list_project_areas", "{}") }));
                yield break;
            }

            var answer = "Answer: " + prompt;
            yield return new AgentModelUpdate(TextDelta: answer);
            yield return new AgentModelUpdate(
                CompletedResponse: new AgentModelResponse(answer, Array.Empty<AgentToolCall>()));
        }
    }
}
