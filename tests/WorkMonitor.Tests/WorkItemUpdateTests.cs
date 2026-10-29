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

public sealed class WorkItemUpdateTests
{
    private const string Arguments = """
        {"id":42,"expectedRevision":7,"fields":{"priority":2}}
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AllSixFieldsUseAllowlistedGuardedPatchAndPreserveUnrelatedFields()
    {
        var handler = new WorkItemHandler();
        using var http = new HttpClient(handler);
        var client = new MonitorClient("test-token", http);

        var result = await client.UpdateWorkItem(42, 7, new WorkItemFieldUpdate
        {
            Title = "New title",
            Description = "<p>New description</p>",
            State = "Resolved",
            AssignedTo = "engineer@example.com",
            Priority = 2,
            Tags = new[] { "customer", " triage ", "CUSTOMER" }
        });

        Assert.Equal(42, result.id);
        Assert.Equal(8, result.rev);
        Assert.Equal(7, handler.Operations.Length);
        Assert.Equal(
            new[]
            {
                "/rev", "/fields/System.Title", "/fields/System.Description",
                "/fields/System.State", "/fields/System.AssignedTo",
                "/fields/Microsoft.VSTS.Common.Priority", "/fields/System.Tags"
            },
            handler.Operations.Select(operation => operation.GetProperty("path").GetString()));
        Assert.Equal("New title", handler.Fields["System.Title"].GetString());
        Assert.Equal("<p>New description</p>", handler.Fields["System.Description"].GetString());
        Assert.Equal("Resolved", handler.Fields["System.State"].GetString());
        Assert.Equal("engineer@example.com", handler.Fields["System.AssignedTo"].GetString());
        Assert.Equal(2, handler.Fields["Microsoft.VSTS.Common.Priority"].GetInt32());
        Assert.Equal("customer; triage", handler.Fields["System.Tags"].GetString());
        Assert.Equal("Unrelated custom tags", handler.Fields["Microsoft.DevDiv.AllTags"].GetString());
        Assert.Equal("Original area", handler.Fields["System.AreaPath"].GetString());
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("""{"title":"New title"}""", "System.Title")]
    [InlineData("""{"description":"<p>Description</p>"}""", "System.Description")]
    [InlineData("""{"state":"Resolved"}""", "System.State")]
    [InlineData("""{"assignedTo":"engineer@example.com"}""", "System.AssignedTo")]
    [InlineData("""{"priority":1}""", "Microsoft.VSTS.Common.Priority")]
    [InlineData("""{"priority":0}""", "Microsoft.VSTS.Common.Priority")]
    [InlineData("""{"tags":["customer"]}""", "System.Tags")]
    [InlineData("""{"title":null,"description":null,"state":null,"assignedTo":null,"priority":3,"tags":null}""", "Microsoft.VSTS.Common.Priority")]
    public async Task AnySingleFieldCanBeUpdatedWithoutSendingOtherFields(string json, string field)
    {
        var handler = new WorkItemHandler();
        using var http = new HttpClient(handler);
        var client = new MonitorClient("test-token", http);
        var fields = JsonSerializer.Deserialize<WorkItemFieldUpdate>(json, JsonOptions)!;

        await client.UpdateWorkItem(42, 7, fields);

        Assert.Equal(2, handler.Operations.Length);
        Assert.Equal("/fields/" + field, handler.Operations[1].GetProperty("path").GetString());
    }

    [Fact]
    public async Task ClearingAlreadyUnsetFieldsUsesAddNotRemove()
    {
        var handler = new WorkItemHandler();
        using var http = new HttpClient(handler);
        var client = new MonitorClient("test-token", http);
        Assert.False(handler.Fields.ContainsKey("System.Description"));
        Assert.False(handler.Fields.ContainsKey("System.AssignedTo"));
        Assert.False(handler.Fields.ContainsKey("System.Tags"));

        await client.UpdateWorkItem(42, 7, new WorkItemFieldUpdate
        {
            Description = "",
            AssignedTo = "",
            Tags = Array.Empty<string>()
        });

        Assert.Equal(4, handler.Operations.Length);
        Assert.All(handler.Operations.Skip(1), operation =>
        {
            Assert.Equal("add", operation.GetProperty("op").GetString());
            Assert.Equal("", operation.GetProperty("value").GetString());
        });
        Assert.Equal("Original title", handler.Fields["System.Title"].GetString());
        Assert.Equal("Active", handler.Fields["System.State"].GetString());
    }

    [Fact]
    public async Task StaleRevisionCannotPartiallyApplyOrRetryTheUpdate()
    {
        var handler = new WorkItemHandler { Revision = 8 };
        using var http = new HttpClient(handler);
        var client = new MonitorClient("test-token", http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UpdateWorkItem(42, 7, new WorkItemFieldUpdate { Title = "Stale edit" }));

        Assert.Equal(HttpStatusCode.PreconditionFailed, error.StatusCode);
        Assert.Contains("Revision mismatch", error.Message);
        Assert.Contains("fresh expectedRevision", error.Message);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(8, handler.Revision);
        Assert.Equal("Original title", handler.Fields["System.Title"].GetString());
    }

    [Fact]
    public async Task PriorityAcceptsZeroAndDefersTypeSpecificUpperLimitsToAzureDevOps()
    {
        Assert.Equal(0, new WorkItemFieldUpdate { Priority = 0 }.ToFieldValues()["Microsoft.VSTS.Common.Priority"]);
        Assert.Equal(4, new WorkItemFieldUpdate { Priority = 4 }.ToFieldValues()["Microsoft.VSTS.Common.Priority"]);
        var schema = new UpdateWorkItemTool().ParametersSchema.GetProperty("properties")
            .GetProperty("fields").GetProperty("properties").GetProperty("priority");
        Assert.Equal(0, schema.GetProperty("minimum").GetInt32());
        Assert.False(schema.TryGetProperty("maximum", out _));
        var handler = new WorkItemHandler
        {
            FailureStatus = HttpStatusCode.BadRequest,
            FailureMessage = "FeedbackTicket priority allows only 0, 1, 2."
        };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new MonitorClient("test-token", http).UpdateWorkItem(42, 7, new WorkItemFieldUpdate { Priority = 3 }));

        Assert.Contains("FeedbackTicket priority allows only 0, 1, 2", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "State transition is not allowed.")]
    [InlineData(HttpStatusCode.Unauthorized, "Authentication is required.")]
    [InlineData(HttpStatusCode.Forbidden, "Edit work items permission is denied.")]
    [InlineData(HttpStatusCode.NotFound, "Work item does not exist.")]
    [InlineData(HttpStatusCode.Conflict, "The work item has changed.")]
    public async Task AzureDevOpsValidationAndPermissionErrorsAreSurfaced(
        HttpStatusCode status, string message)
    {
        var handler = new WorkItemHandler { FailureStatus = status, FailureMessage = message };
        using var http = new HttpClient(handler);
        var client = new MonitorClient("test-token", http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UpdateWorkItem(42, 7, new WorkItemFieldUpdate { Priority = 2 }));

        Assert.Equal(status, error.StatusCode);
        Assert.Contains(message, error.Message);
        Assert.Equal(7, handler.Revision);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"id":42,"fields":{"priority":2}}""")]
    [InlineData("""{"id":0,"expectedRevision":7,"fields":{"priority":2}}""")]
    [InlineData("""{"id":42,"expectedRevision":0,"fields":{"priority":2}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":null}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"title":null}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"title":""}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"title":" "}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"state":""}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"assignedTo":" "}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"priority":-1}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"priority":"2"}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"tags":"customer"}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"tags":[""]}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"tags":[null]}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"tags":["one;two"]}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"title":"Valid","areaPath":"Not allowed"}}""")]
    [InlineData("""{"id":42,"expectedRevision":7,"fields":{"priority":2},"relations":[]}""")]
    public async Task InvalidInputIsRejectedEvenInDryRunBeforeAuthentication(string json)
    {
        var tool = new UpdateWorkItemTool((_, _) =>
            throw new InvalidOperationException("The client must not be created for invalid input."));

        var error = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(Json(json), new AgentContext(), CancellationToken.None));

        Assert.True(error is ArgumentException or JsonException, error?.ToString() ?? "Invalid update was accepted.");
    }

    [Fact]
    public void TitleLengthLimitIsEnforcedExactly()
    {
        Assert.Equal(new string('t', 255),
            new WorkItemFieldUpdate { Title = new string('t', 255) }.ToFieldValues()["System.Title"]);
        Assert.Throws<ArgumentException>(() =>
            new WorkItemFieldUpdate { Title = new string('t', 256) }.ToFieldValues());
    }

    [Fact]
    public async Task DryRunPreviewsOnlyRequestedValuesWithoutResolvingCredentials()
    {
        var tool = new UpdateWorkItemTool();
        var context = new AgentContext
        {
            UserName = "test-user",
            DevOpsTokenProvider = _ => throw new InvalidOperationException("Dry run must not authenticate.")
        };

        var result = JsonSerializer.SerializeToElement(
            await tool.ExecuteAsync(Json(Arguments), context, CancellationToken.None));

        Assert.True(tool.IsWriteTool);
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(7, result.GetProperty("expectedRevision").GetInt32());
        Assert.Equal(2, result.GetProperty("fields").GetProperty("Microsoft.VSTS.Common.Priority").GetInt32());
        Assert.Single(result.GetProperty("fields").EnumerateObject());
        Assert.Contains("[dryRun]", Assert.Single(context.AuditLog));
    }

    [Fact]
    public async Task LiveUpdateWithoutAuthenticationIsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UpdateWorkItemTool().ExecuteAsync(
                Json(Arguments), new AgentContext { DryRun = false }, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeDispatchDoesNotWrite()
    {
        var handler = new WorkItemHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MonitorClient("test-token", http).UpdateWorkItem(
                42, 7, new WorkItemFieldUpdate { Priority = 2 }, cancellation.Token));

        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AgentWriteApprovalAndAuditApplyToFieldUpdates(bool dryRun, bool approve)
    {
        var handler = new WorkItemHandler();
        using var http = new HttpClient(handler);
        var tool = Tool(http);
        var approval = new RecordingApproval(approve);
        var model = new FakeAgentModel();
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse(
            "", new[] { new AgentToolCall("update-1", tool.Name, Arguments) })));
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse(
            "Finished.", Array.Empty<AgentToolCall>())));
        var service = new AgentService(
            model, new AgentToolRegistry(new[] { tool }), approval,
            NullLogger<AgentService>.Instance);
        var context = new AgentContext
        {
            DryRun = dryRun, UserName = "test-user", DevOpsToken = "test-token", SessionId = "test-session"
        };

        var result = await service.RunAsync("Change the priority to 2", null, context);

        Assert.Equal(dryRun ? 0 : 1, approval.Requests.Count);
        Assert.Equal(!dryRun && approve ? 1 : 0, handler.RequestCount);
        if (!dryRun)
            Assert.Equal(Arguments, Assert.Single(approval.Requests).ArgumentsJson);
        Assert.Contains(result.AuditLog, entry => entry.Contains(
            dryRun ? "[dryRun]" : approve ? "Updated work item 42 to revision 8" : "Denied write tool"));
        if (!dryRun && approve)
            Assert.Contains(result.AuditLog, entry => entry.Contains("by test-user"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task McpRequiresBothServerWriteEnablementAndPerCallConfirmation(bool allowWrites, bool confirm)
    {
        var handler = new WorkItemHandler();
        using var http = new HttpClient(handler);
        var credential = new TestCredential();
        var tools = McpTools(Tool(http), credential, allowWrites);

        var result = JsonSerializer.SerializeToElement(
            await tools.UpdateWorkItemAsync(42, 7, new WorkItemFieldUpdate { Priority = 2 }, confirm));

        var shouldWrite = allowWrites && confirm;
        Assert.Equal(shouldWrite ? 1 : 0, handler.RequestCount);
        Assert.Equal(shouldWrite ? 1 : 0, credential.RequestCount);
        Assert.True(result.GetProperty(shouldWrite ? "updated" : "dryRun").GetBoolean());
    }

    [Fact]
    public async Task McpSurfacesValidationAndConflictDetailsWithoutSuccessAudit()
    {
        var handler = new WorkItemHandler { Revision = 8 };
        using var http = new HttpClient(handler);
        var tools = McpTools(Tool(http), new TestCredential(), allowWrites: true);

        var validation = await Assert.ThrowsAsync<McpException>(() =>
            tools.UpdateWorkItemAsync(42, 7, new WorkItemFieldUpdate(), confirm: true));
        Assert.Contains("At least one field", validation.Message);
        Assert.Equal(0, handler.RequestCount);
        var conflict = await Assert.ThrowsAsync<McpException>(() =>
            tools.UpdateWorkItemAsync(42, 7, new WorkItemFieldUpdate { Priority = 2 }, confirm: true));
        Assert.Contains("Revision mismatch", conflict.Message);
        Assert.Equal(1, handler.RequestCount);

        var context = new AgentContext { DryRun = false, DevOpsToken = "test-token" };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Tool(http).ExecuteAsync(Json(Arguments), context, CancellationToken.None));
        Assert.Contains(context.AuditLog, entry => entry.Contains("Attempting update"));
        Assert.DoesNotContain(context.AuditLog, entry => entry.StartsWith("Updated work item"));
    }

    [Fact]
    public void WorkItemReadsRequestStandardTagsAndDeserializeRevision()
    {
        Assert.Contains("System.Tags", new WorkItemRequest(new List<int> { 42 }).fields);
        var item = Newtonsoft.Json.JsonConvert.DeserializeObject<InternalItem>(
            """{"id":42,"rev":7,"fields":{"System.Title":"Title","System.Tags":"one; two","Microsoft.DevDiv.AllTags":"custom"}}""")!;
        Assert.Equal(7, item.rev);
        Assert.Equal("one; two", item.fields.Tags);
    }

    [Fact]
    public async Task WorkItemDetailsExposeRevisionAndAllEditableFields()
    {
        using var http = new HttpClient(new WorkItemReadHandler());

        var result = JsonSerializer.SerializeToElement(
            await new MonitorService("test-token", http).GetWorkItemDetails(42));

        Assert.Equal(42, result.GetProperty("id").GetInt32());
        Assert.Equal(7, result.GetProperty("revision").GetInt32());
        Assert.Equal("Original title", result.GetProperty("title").GetString());
        Assert.Equal("<p>Details</p>", result.GetProperty("description").GetString());
        Assert.Equal("Active", result.GetProperty("state").GetString());
        Assert.Equal("engineer@example.com", result.GetProperty("assignedTo").GetProperty("uniqueName").GetString());
        Assert.Equal(2, result.GetProperty("priority").GetInt32());
        Assert.Equal("one; two", result.GetProperty("tags").GetString());
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static UpdateWorkItemTool Tool(HttpClient http)
        => new(async (context, token) =>
            new MonitorClient(await context.GetRequiredDevOpsTokenAsync(token), http));

    private static WorkMonitorMcpTools McpTools(IAgentTool tool, TestCredential credential, bool allowWrites)
        => new(new AgentToolRegistry(new[] { tool }), new DevOpsTokenService(credential),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkMonitor:Mcp:AllowWrites"] = allowWrites.ToString()
            }).Build());

    private sealed class RecordingApproval(bool approve) : IWriteApprovalHandler
    {
        public List<WriteApprovalRequest> Requests { get; } = new();

        public ValueTask<bool> ApproveAsync(WriteApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(approve);
        }
    }

    private sealed class TestCredential : TokenCredential
    {
        public int RequestCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestCount++;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class WorkItemReadHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string response;
            if (request.RequestUri!.AbsolutePath.EndsWith("/comments"))
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                response = """{"comments":[]}""";
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Contains(body.RootElement.GetProperty("fields").EnumerateArray(),
                    field => field.GetString() == "System.Tags");
                response = """
                    {"value":[{"id":42,"rev":7,"fields":{
                      "System.Title":"Original title","System.Description":"<p>Details</p>",
                      "System.State":"Active","System.AssignedTo":{"uniqueName":"engineer@example.com"},
                      "Microsoft.VSTS.Common.Priority":2,"System.Tags":"one; two",
                      "Microsoft.DevDiv.AllTags":"Not standard tags"
                    }}]}
                    """;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class WorkItemHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int Revision { get; set; } = 7;
        public HttpStatusCode? FailureStatus { get; init; }
        public string FailureMessage { get; init; } = "";
        public JsonElement[] Operations { get; private set; } = Array.Empty<JsonElement>();
        public Dictionary<string, JsonElement> Fields { get; } = new()
        {
            ["System.Title"] = JsonSerializer.SerializeToElement("Original title"),
            ["System.State"] = JsonSerializer.SerializeToElement("Active"),
            ["System.AreaPath"] = JsonSerializer.SerializeToElement("Original area"),
            ["Microsoft.DevDiv.AllTags"] = JsonSerializer.SerializeToElement("Unrelated custom tags")
        };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/_apis/wit/workitems/42?api-version=7.1", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("application/json-patch+json", request.Content!.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Operations = body.RootElement.EnumerateArray().Select(operation => operation.Clone()).ToArray();
            Assert.Equal("test", Operations[0].GetProperty("op").GetString());
            Assert.Equal("/rev", Operations[0].GetProperty("path").GetString());
            if (FailureStatus.HasValue)
                return Response(FailureStatus.Value, new { message = FailureMessage });
            if (Operations[0].GetProperty("value").GetInt32() != Revision)
                return Response(HttpStatusCode.PreconditionFailed, new { message = "Revision mismatch." });
            foreach (var operation in Operations.Skip(1))
            {
                Assert.Equal("add", operation.GetProperty("op").GetString());
                var path = operation.GetProperty("path").GetString()!;
                Assert.StartsWith("/fields/", path);
                Fields[path["/fields/".Length..]] = operation.GetProperty("value").Clone();
            }

            return Response(HttpStatusCode.OK, new { id = 42, rev = ++Revision });
        }

        private static HttpResponseMessage Response(HttpStatusCode status, object value)
            => new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
