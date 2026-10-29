using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Models;
using WorkMonitor.Core.Services;
using WorkMonitor.Mcp;

namespace WorkMonitor.Tests;

public sealed class BugCreationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static BugCreationFields Bug => new()
    {
        Title = "Engineering Bug",
        AreaPath = @"DevDiv\NET Tools\Project",
        ReproSteps = "<p>Steps, expected behavior, actual behavior.</p>"
    };

    [Fact]
    public async Task CreatesBugAndRelatedLinkInOneRequestThenVerifiesPersistedRelationship()
    {
        var handler = new BugHandler();
        using var http = new HttpClient(handler);
        var context = LiveContext();
        var result = JsonSerializer.SerializeToElement(await Tool(http).ExecuteAsync(
            Arguments(Bug with
            {
                Description = "<p>Separate description</p>",
                IterationPath = "DevDiv",
                AssignedTo = "engineer@example.com",
                Priority = 0,
                Tags = new[] { "customer" },
                Source = "Other Devdiv Teams"
            }), context, CancellationToken.None));

        Assert.True(result.GetProperty("created").GetBoolean());
        Assert.Equal(900, result.GetProperty("id").GetInt32());
        Assert.EndsWith("/_workitems/edit/900", result.GetProperty("url").GetString());
        Assert.True(result.GetProperty("relation").GetProperty("verified").GetBoolean());
        Assert.Equal(BugCreationFields.FeedbackRelation, result.GetProperty("relation").GetProperty("rel").GetString());
        Assert.Equal(42, result.GetProperty("feedbackTicketId").GetInt32());
        Assert.Equal(new[] { "source", "metadata", "validate", "create", "verify" }, handler.Stages);
        Assert.Equal(1, handler.CreateAttempts);
        Assert.Equal(1, handler.CreatedCount);
        var fields = handler.Patch.Where(operation => operation.GetProperty("path").GetString()!.StartsWith("/fields/"))
            .ToDictionary(operation => operation.GetProperty("path").GetString()!, operation => operation.GetProperty("value"));
        Assert.Equal(9, fields.Count);
        Assert.Equal(Bug.ReproSteps, fields["/fields/Microsoft.VSTS.TCM.ReproSteps"].GetString());
        Assert.Equal("<p>Separate description</p>", fields["/fields/System.Description"].GetString());
        Assert.Equal(@"DevDiv\NET Tools\Project", fields["/fields/System.AreaPath"].GetString());
        Assert.Equal("DevDiv", fields["/fields/System.IterationPath"].GetString());
        Assert.Equal("engineer@example.com", fields["/fields/System.AssignedTo"].GetString());
        Assert.Equal(0, fields["/fields/Microsoft.VSTS.Common.Priority"].GetInt32());
        Assert.Equal("Other Devdiv Teams", fields["/fields/Microsoft.DevDiv.Source"].GetString());
        Assert.Equal("customer", fields["/fields/System.Tags"].GetString());
        Assert.DoesNotContain("/fields/System.State", fields.Keys);
        Assert.Contains(context.AuditLog, entry => entry.Contains("Created Bug 900") && entry.Contains("verified Related") && entry.Contains("by test-user"));
    }

    [Fact]
    public async Task DefaultsAreLeftToTheProcessAndDescriptionIsNotUsedAsReproSteps()
    {
        var handler = new BugHandler();
        using var http = new HttpClient(handler);

        await new MonitorClient("test-token", http).CreateBugFromFeedback(42, Bug);

        Assert.Equal(4, handler.Patch.Length);
        Assert.DoesNotContain(handler.Patch, operation =>
            operation.GetProperty("path").GetString() is
                "/fields/System.State" or "/fields/Microsoft.DevDiv.Source" or
                "/fields/System.Description" or "/fields/System.AreaId" or "/fields/System.IterationId");
    }

    [Theory]
    [InlineData("Bug")]
    [InlineData("Task")]
    [InlineData("Feedback")]
    [InlineData("")]
    public async Task RejectsSourceThatIsNotFeedbackTicketBeforeAnyCreation(string sourceType)
    {
        var handler = new BugHandler { SourceType = sourceType };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new MonitorClient("test-token", http).CreateBugFromFeedback(42, Bug));

        Assert.Contains("not a verified FeedbackTicket", error.Message);
        Assert.Equal(new[] { "source" }, handler.Stages);
        Assert.Equal(0, handler.CreateAttempts);
    }

    [Fact]
    public async Task RejectsMismatchedSourceId()
    {
        var handler = new BugHandler { SourceId = 43 };
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new MonitorClient("test-token", http).CreateBugFromFeedback(42, Bug));

        Assert.Equal(0, handler.CreateAttempts);
    }

    [Theory]
    [InlineData("https://untrusted.invalid/devdiv/devdiv/_apis/wit/workItems/42")]
    [InlineData("https://user@dev.azure.com/devdiv/devdiv/_apis/wit/workItems/42")]
    [InlineData("https://dev.azure.com/other/devdiv/_apis/wit/workItems/42")]
    [InlineData("https://dev.azure.com/devdiv/unexpected-project/_apis/wit/workItems/42")]
    [InlineData("https://dev.azure.com/devdiv/devdiv/_apis/wit/workItems/43")]
    [InlineData("https://dev.azure.com/devdiv/devdiv/_apis/wit/workItems/42?unexpected=true")]
    [InlineData("https://dev.azure.com/devdiv/devdiv/_apis/wit/workItems/42#fragment")]
    public async Task RejectsUntrustedSourceUrlBeforeCreating(string sourceUrl)
    {
        var handler = new BugHandler { SourceUrl = sourceUrl };
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MonitorClient("test-token", http).CreateBugFromFeedback(42, Bug));

        Assert.Equal(0, handler.CreateAttempts);
    }

    [Theory]
    [InlineData("{}", "arguments")]
    [InlineData("""{"feedbackTicketId":0,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":"Steps"}}""", "id")]
    [InlineData("""{"feedbackTicketId":-1,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":"Steps"}}""", "id")]
    [InlineData("""{"feedbackTicketId":42,"bug":null}""", "bug")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"areaPath":"DevDiv","reproSteps":"Steps"}}""", "title")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"","areaPath":"DevDiv","reproSteps":"Steps"}}""", "title")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":null,"areaPath":"DevDiv","reproSteps":"Steps"}}""", "title")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","reproSteps":"Steps"}}""", "area")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":" ","reproSteps":"Steps"}}""", "area")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":"DevDiv","description":"Not repro steps"}}""", "repro")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":" "}}""", "repro")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":"Steps","priority":-1}}""", "priority")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":"Steps","tags":["one;two"]}}""", "tags")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":"Steps","source":" "}}""", "source")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":"Steps","state":"Active"}}""", "unknown")]
    [InlineData("""{"feedbackTicketId":42,"bug":{"title":"T","areaPath":"DevDiv","reproSteps":"Steps"},"relations":[]}""", "unknown")]
    public async Task InvalidInputFailsLocallyEvenInDryRun(string json, string scenario)
    {
        var tool = new CreateBugFromFeedbackTool((_, _) =>
            throw new InvalidOperationException("Invalid inputs must not create a client."));
        using var document = JsonDocument.Parse(json);

        var error = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(document.RootElement, new AgentContext(), CancellationToken.None));

        Assert.True(error is ArgumentException or JsonException, $"{scenario}: {error}");
    }

    [Fact]
    public async Task DryRunDoesNotResolveAuthenticationOrClaimSourceValidation()
    {
        var context = new AgentContext
        {
            DevOpsTokenProvider = _ => throw new InvalidOperationException("Dry run must not authenticate.")
        };
        var tool = new CreateBugFromFeedbackTool();

        var result = JsonSerializer.SerializeToElement(
            await tool.ExecuteAsync(Arguments(Bug), context, CancellationToken.None));

        Assert.True(tool.IsWriteTool);
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(Bug.ReproSteps, result.GetProperty("fields").GetProperty("Microsoft.VSTS.TCM.ReproSteps").GetString());
        Assert.Equal("System.LinkTypes.Related", result.GetProperty("relation").GetProperty("rel").GetString());
        Assert.EndsWith("/workitems/42", result.GetProperty("relation").GetProperty("url").GetString());
        Assert.Contains("NOT been checked", result.GetProperty("message").GetString());
        Assert.Contains("[dryRun]", Assert.Single(context.AuditLog));
    }

    [Theory]
    [InlineData("priority")]
    [InlineData("source")]
    [InlineData("missing-field")]
    public async Task MetadataRejectsInvalidBugInputsWithoutCreation(string scenario)
    {
        var handler = new BugHandler { OmittedMetadataField = scenario == "missing-field" ? "Microsoft.VSTS.TCM.ReproSteps" : null };
        using var http = new HttpClient(handler);
        var bug = scenario switch
        {
            "priority" => Bug with { Priority = 4 },
            "source" => Bug with { Source = "Guessed customer category" },
            _ => Bug
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new MonitorClient("test-token", http).CreateBugFromFeedback(42, bug));

        Assert.Contains("No Bug was created", error.Message);
        Assert.Equal(new[] { "source", "metadata" }, handler.Stages);
        Assert.Equal(0, handler.CreateAttempts);
    }

    [Theory]
    [InlineData("source", HttpStatusCode.NotFound, "Source does not exist.")]
    [InlineData("metadata", HttpStatusCode.Forbidden, "Process metadata access denied.")]
    [InlineData("validate", HttpStatusCode.BadRequest, "Custom.RequiredInput is required.")]
    [InlineData("validate", HttpStatusCode.BadRequest, "Engineering area path is invalid.")]
    [InlineData("create", HttpStatusCode.Forbidden, "Create work items permission denied.")]
    [InlineData("create", HttpStatusCode.BadRequest, "Related link validation failed.")]
    public async Task ProcessAndPermissionFailuresAreVisibleAndCannotCreateUnlinkedBug(
        string stage, HttpStatusCode status, string message)
    {
        var handler = new BugHandler { RejectedStage = stage, RejectedStatus = status, ErrorMessage = message };
        using var http = new HttpClient(handler);
        var context = LiveContext();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Tool(http).ExecuteAsync(Arguments(Bug), context, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Contains(message, error.Message);
        Assert.Equal(stage == "create" ? 1 : 0, handler.CreateAttempts);
        Assert.Equal(0, handler.CreatedCount);
        Assert.DoesNotContain(context.AuditLog, entry => entry.StartsWith("Created Bug"));
        Assert.Contains(context.AuditLog, entry => entry.StartsWith("Bug creation failed"));
    }

    [Theory]
    [InlineData("transport", false)]
    [InlineData("timeout", false)]
    [InlineData("server-error", false)]
    [InlineData("invalid-json", false)]
    [InlineData("missing-id", false)]
    [InlineData("verify-error", true)]
    [InlineData("wrong-relation", true)]
    [InlineData("wrong-direction", true)]
    [InlineData("wrong-type", true)]
    [InlineData("wrong-id", true)]
    [InlineData("wrong-url", true)]
    [InlineData("other-project", true)]
    [InlineData("malformed-fields", true)]
    public async Task UncertainOutcomeIsAnErrorAndNeverTriggersAnotherCreation(string outcome, bool idKnown)
    {
        var handler = new BugHandler { Outcome = outcome };
        using var http = new HttpClient(handler);
        var context = LiveContext();

        var error = await Assert.ThrowsAsync<BugCreationOutcomeUnknownException>(() =>
            Tool(http).ExecuteAsync(Arguments(Bug), context, CancellationToken.None));

        Assert.Contains("DO NOT retry creation automatically", error.Message);
        Assert.Equal(idKnown ? 900 : null, error.ReportedBugId);
        if (idKnown)
            Assert.EndsWith("/_workitems/edit/900", error.ReportedBugUrl);
        Assert.Equal(1, handler.CreateAttempts);
        Assert.DoesNotContain(context.AuditLog, entry => entry.StartsWith("Created Bug"));
        Assert.Contains(context.AuditLog, entry => entry.StartsWith("UNCONFIRMED"));
    }

    [Fact]
    public async Task CancellationBeforeDispatchPreventsCreationButAfterDispatchStillObservesOutcome()
    {
        using var before = new CancellationTokenSource();
        var rejected = new BugHandler { AfterValidation = before.Cancel };
        using var rejectedHttp = new HttpClient(rejected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MonitorClient("test-token", rejectedHttp).CreateBugFromFeedback(42, Bug, before.Token));
        Assert.Equal(0, rejected.CreateAttempts);

        using var after = new CancellationTokenSource();
        var accepted = new BugHandler { AfterCreate = after.Cancel };
        using var acceptedHttp = new HttpClient(accepted);
        var result = await new MonitorClient("test-token", acceptedHttp).CreateBugFromFeedback(42, Bug, after.Token);
        Assert.True(after.IsCancellationRequested);
        Assert.Equal(900, result.Id);
        Assert.Equal(new[] { "source", "metadata", "validate", "create", "verify" }, accepted.Stages);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task McpCreationRequiresBothWriteGates(bool allowWrites, bool confirm)
    {
        var handler = new BugHandler();
        using var http = new HttpClient(handler);
        var credential = new TestCredential();
        var tools = McpTools(Tool(http), credential, allowWrites);

        var result = JsonSerializer.SerializeToElement(
            await tools.CreateBugFromFeedbackAsync(42, Bug, confirm));

        var writes = allowWrites && confirm;
        Assert.Equal(writes ? 1 : 0, handler.CreatedCount);
        Assert.Equal(writes ? 1 : 0, credential.RequestCount);
        Assert.True(result.GetProperty(writes ? "created" : "dryRun").GetBoolean());
    }

    [Fact]
    public async Task McpPreservesUncertainOutcomeAndReportedId()
    {
        var handler = new BugHandler { Outcome = "wrong-relation" };
        using var http = new HttpClient(handler);
        var tools = McpTools(Tool(http), new TestCredential(), allowWrites: true);

        var error = await Assert.ThrowsAsync<McpException>(() =>
            tools.CreateBugFromFeedbackAsync(42, Bug, confirm: true));

        Assert.Contains("DO NOT retry", error.Message);
        Assert.Contains("Reported Bug 900", error.Message);
        Assert.Equal(1, handler.CreateAttempts);
    }

    [Fact]
    public async Task AgentCannotAutomaticallyRetryCreationAfterUncertainOutcome()
    {
        var handler = new BugHandler { Outcome = "transport" };
        using var http = new HttpClient(handler);
        var tool = Tool(http);
        var model = new FakeAgentModel();
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse(
            "", new[]
            {
                new AgentToolCall("create-1", tool.Name, Arguments(Bug).GetRawText()),
                new AgentToolCall("create-retry", tool.Name, Arguments(Bug).GetRawText())
            })));
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse(
            "The outcome needs verification.", Array.Empty<AgentToolCall>())));
        var service = new AgentService(model, new AgentToolRegistry(new[] { tool }),
            new AllowAllWriteApprovalHandler(), NullLogger<AgentService>.Instance);
        var context = LiveContext();

        await service.RunAsync("Create one linked Bug", null, context);

        Assert.Equal(1, handler.CreateAttempts);
        Assert.Equal(1, handler.CreatedCount);
        var retry = model.Requests[1].Single(message => message.ToolCallId == "create-retry");
        Assert.Contains("Blocked automatic Bug creation retry", retry.Content);
        Assert.Contains(context.AuditLog, entry => entry.StartsWith("Blocked automatic Bug creation retry"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CliRequiresApprovalAndAuditsTheCreation(bool dryRun, bool approve)
    {
        var handler = new BugHandler();
        using var http = new HttpClient(handler);
        var tool = Tool(http);
        var model = new FakeAgentModel();
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse(
            "", new[] { new AgentToolCall("create-1", tool.Name, Arguments(Bug).GetRawText()) })));
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse(
            "Finished.", Array.Empty<AgentToolCall>())));
        var service = new AgentService(model, new AgentToolRegistry(new[] { tool }),
            approve ? new AllowAllWriteApprovalHandler() : new DenyWriteApprovalHandler(),
            NullLogger<AgentService>.Instance);
        var context = new AgentContext { DryRun = dryRun, DevOpsToken = "test-token", UserName = "test-user" };

        var result = await service.RunAsync("Create a Bug linked to this feedback", null, context);

        Assert.Equal(!dryRun && approve ? 1 : 0, handler.CreatedCount);
        Assert.Contains(result.AuditLog, entry => entry.Contains(
            dryRun ? "[dryRun]" : approve ? "Created Bug 900" : "Denied write tool"));
        if (!dryRun && approve)
            Assert.Contains(result.AuditLog, entry => entry.Contains("Approved write tool"));
    }

    private static JsonElement Arguments(BugCreationFields bug) =>
        JsonSerializer.SerializeToElement(new { feedbackTicketId = 42, bug }, JsonOptions);

    private static AgentContext LiveContext() => new() { DryRun = false, DevOpsToken = "test-token", UserName = "test-user" };

    private static CreateBugFromFeedbackTool Tool(HttpClient http) =>
        new(async (context, token) => new MonitorClient(await context.GetRequiredDevOpsTokenAsync(token), http));

    private static WorkMonitorMcpTools McpTools(IAgentTool tool, TestCredential credential, bool allowWrites) =>
        new(new AgentToolRegistry(new[] { tool }), new DevOpsTokenService(credential),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkMonitor:Mcp:AllowWrites"] = allowWrites.ToString()
            }).Build());

    private sealed class TestCredential : TokenCredential
    {
        public int RequestCount { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestCount++;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class BugHandler : HttpMessageHandler
    {
        public List<string> Stages { get; } = new();
        public int CreateAttempts { get; private set; }
        public int CreatedCount { get; private set; }
        public string SourceType { get; init; } = "FeedbackTicket";
        public int SourceId { get; init; } = 42;
        public string SourceUrl { get; init; } = SourceApiUrl;
        public string? OmittedMetadataField { get; init; }
        public string? RejectedStage { get; init; }
        public HttpStatusCode RejectedStatus { get; init; } = HttpStatusCode.BadRequest;
        public string ErrorMessage { get; init; } = "";
        public string Outcome { get; init; } = "";
        public Action? AfterValidation { get; init; }
        public Action? AfterCreate { get; init; }
        public JsonElement[] Patch { get; private set; } = Array.Empty<JsonElement>();
        private string? _validatedPayload;
        private const string SourceApiUrl = "https://dev.azure.com/devdiv/00000000-0000-0000-0000-000000000001/_apis/wit/workItems/42";
        private const string BugApiUrl = "https://dev.azure.com/devdiv/00000000-0000-0000-0000-000000000001/_apis/wit/workItems/900";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            var uri = request.RequestUri!;
            var stage = request.Method == HttpMethod.Post
                ? uri.Query.Contains("validateOnly=true") ? "validate" : "create"
                : uri.AbsolutePath.EndsWith("/fields") ? "metadata"
                : uri.AbsolutePath.EndsWith("/42") ? "source" : "verify";
            Stages.Add(stage);
            if (stage == "create")
                CreateAttempts++;
            if (stage == RejectedStage)
                return Response(RejectedStatus, new { message = ErrorMessage });
            if (stage == "source")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/devdiv/devdiv/_apis/wit/workitems/42", uri.AbsolutePath);
                return Response(HttpStatusCode.OK, new
                {
                    id = SourceId,
                    url = SourceUrl,
                    fields = new Dictionary<string, object>
                    {
                        ["System.WorkItemType"] = SourceType,
                        ["System.TeamProject"] = "DevDiv"
                    }
                });
            }
            if (stage == "metadata")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Contains("workitemtypes/Bug/fields", uri.AbsolutePath);
                Assert.Contains("$expand=All", uri.Query);
                var definitions = new[]
                {
                    "System.Title", "System.AreaPath", "Microsoft.VSTS.TCM.ReproSteps", "System.Description",
                    "System.IterationPath", "System.AssignedTo", "Microsoft.VSTS.Common.Priority",
                    "System.Tags", "Microsoft.DevDiv.Source", "System.AreaId", "System.IterationId", "System.State"
                }.Where(field => field != OmittedMetadataField).Select(field => new
                {
                    referenceName = field,
                    alwaysRequired = field is "System.Title" or "System.State" or "System.AreaId" or "System.IterationId" or "Microsoft.DevDiv.Source",
                    defaultValue = field == "Microsoft.DevDiv.Source" ? "PU/Feature team" : field == "System.State" ? "Active" : null,
                    allowedValues = field == "Microsoft.VSTS.Common.Priority" ? new[] { "0", "1", "2", "3" }
                        : field == "Microsoft.DevDiv.Source" ? new[] { "PU/Feature team", "Other Devdiv Teams" } : Array.Empty<string>()
                });
                return Response(HttpStatusCode.OK, new { value = definitions });
            }
            if (stage is "validate" or "create")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/workitems/$Bug", uri.AbsolutePath);
                Assert.Contains("$expand=Relations", uri.Query);
                Assert.DoesNotContain("bypassRules", uri.Query);
                Assert.Equal("application/json-patch+json", request.Content!.Headers.ContentType?.MediaType);
                var payload = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(payload);
                Patch = document.RootElement.EnumerateArray().Select(operation => operation.Clone()).ToArray();
                Assert.All(Patch, operation => Assert.Equal("add", operation.GetProperty("op").GetString()));
                var relation = Assert.Single(Patch.Where(operation => operation.GetProperty("path").GetString() == "/relations/-"));
                Assert.Equal("System.LinkTypes.Related", relation.GetProperty("value").GetProperty("rel").GetString());
                Assert.Equal(SourceApiUrl, relation.GetProperty("value").GetProperty("url").GetString());
                if (stage == "validate")
                {
                    _validatedPayload = payload;
                    AfterValidation?.Invoke();
                    return Response(HttpStatusCode.OK, new { });
                }
                Assert.Equal(_validatedPayload, payload);
                CreatedCount++;
                AfterCreate?.Invoke();
                if (Outcome == "transport")
                    throw new HttpRequestException("Connection ended after submission.");
                if (Outcome == "timeout")
                    throw new OperationCanceledException("Create response timed out.");
                if (Outcome == "server-error")
                    return Response(HttpStatusCode.ServiceUnavailable, new { message = "Service unavailable after submission." });
                if (Outcome == "invalid-json")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid") };
                return Response(HttpStatusCode.OK, Outcome == "missing-id" ? new { } : (object)new { id = 900 });
            }
            Assert.Equal("verify", stage);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/workitems/900", uri.AbsolutePath);
            Assert.Contains("$expand=Relations", uri.Query);
            if (Outcome == "verify-error")
                return Response(HttpStatusCode.Forbidden, new { message = "Cannot read created work item." });
            return Response(HttpStatusCode.OK, new
            {
                id = Outcome == "wrong-id" ? 901 : 900,
                url = Outcome == "wrong-url" ? "https://untrusted.invalid/workitems/900" : BugApiUrl,
                fields = Outcome == "malformed-fields" ? (object)"invalid" : new Dictionary<string, object>
                {
                    ["System.WorkItemType"] = Outcome == "wrong-type" ? "FeedbackTicket" : "Bug"
                },
                relations = new[]
                {
                    new
                    {
                        rel = Outcome == "wrong-relation" ? "System.LinkTypes.Duplicate-Forward" : "System.LinkTypes.Related",
                        url = Outcome == "wrong-direction" ? BugApiUrl
                            : Outcome == "other-project" ? "https://dev.azure.com/devdiv/00000000-0000-0000-0000-000000000002/_apis/wit/workItems/42"
                            : SourceApiUrl
                    }
                }
            });
        }

        private static HttpResponseMessage Response(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
