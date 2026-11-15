using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Tests;

public sealed class WorkItemLookupTests
{
    [Fact]
    public async Task SummaryHasStableCompactContractAndDoesNotFetchDescriptionsOrDiscussions()
    {
        using var source = new LookupSourceHandler();
        var result = await Read(source, """{"id":42,"view":"summary"}""");
        Assert.Equal(
            new[] { "assignedTo", "coverage", "id", "priority", "revision", "state", "title", "url" },
            result.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        AssertLookup(result);
        Assert.Equal("Engineer", result.GetProperty("assignedTo").GetString());
        Assert.Equal("summary", result.GetProperty("coverage").GetProperty("view").GetString());
        Assert.False(result.GetProperty("coverage").GetProperty("isPartial").GetBoolean());
        Assert.Single(source.Paths);
        Assert.Equal(
            new[] { "Microsoft.VSTS.Common.Priority", "System.AssignedTo", "System.Id", "System.State", "System.Title" },
            source.RequestedFields.OrderBy(name => name));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnassignedAndUndefinedPriorityAreNotFabricated(bool summary)
    {
        using var source = new LookupSourceHandler { Assigned = false, Priority = null };
        var result = await Read(source, JsonSerializer.Serialize(new { id = 42, view = summary ? "summary" : "detail" }));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("priority").ValueKind);
        if (summary)
            Assert.Equal("Unassigned", result.GetProperty("assignedTo").GetString());
        else
            Assert.Equal(JsonValueKind.Null, result.GetProperty("assignedTo").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task SummaryUsesIdentityNameWhenDisplayNameIsUnavailable(string? displayName)
    {
        using var source = new LookupSourceHandler { DisplayName = displayName };
        var result = await Read(source, """{"id":42,"view":"summary"}""");
        Assert.Equal("engineer@example.com", result.GetProperty("assignedTo").GetString());
        Assert.Single(source.Paths);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("detail")]
    public async Task PriorityZeroIsPreservedAndMissingPriorityIsNull(string view)
    {
        using var source = new LookupSourceHandler { Priority = 0 };
        var result = await Read(source, JsonSerializer.Serialize(new { id = 42, view }));
        Assert.Equal(0, result.GetProperty("priority").GetInt32());
        source.ItemJson = """{"value":[{"id":42,"rev":7,"fields":{"System.Title":"2026-11-01T00:00:00Z","System.State":"Active"}}]}""";
        result = await Read(source, JsonSerializer.Serialize(new { id = 42, view }));
        Assert.Equal("2026-11-01T00:00:00Z", result.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("priority").ValueKind);
    }

    [Fact]
    public async Task DefaultDetailPreservesRevisionEditableFieldsAndDiscussionShapes()
    {
        using var source = new LookupSourceHandler();
        var result = await Read(source, """{"id":42}""");
        AssertLookup(result);
        Assert.Equal("<p>Details</p>", result.GetProperty("description").GetString());
        Assert.Equal("engineer@example.com", result.GetProperty("assignedTo").GetProperty("uniqueName").GetString());
        Assert.Equal("one; two", result.GetProperty("tags").GetString());
        Assert.Equal("Customer post", result.GetProperty("postTitle").GetString());
        Assert.Equal("Customer content", result.GetProperty("postContent").GetString());
        Assert.Equal("Internal comment", Assert.Single(result.GetProperty("comments").EnumerateArray()).GetProperty("text").GetString());
        var customer = Assert.Single(result.GetProperty("customerComments").EnumerateArray());
        Assert.Equal("Customer", customer.GetProperty("author").GetString());
        Assert.Equal("Customer comment", customer.GetProperty("text").GetString());
        var coverage = result.GetProperty("coverage");
        Assert.Equal("detail", coverage.GetProperty("view").GetString());
        Assert.False(coverage.GetProperty("outputTruncated").GetBoolean());
        Assert.True(coverage.GetProperty("isPartial").GetBoolean());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("customerComments").GetProperty("totalCount").ValueKind);
        Assert.True(coverage.GetProperty("customerComments").GetProperty("isPartial").GetBoolean());
        Assert.Equal(3, source.Paths.Count);
        Assert.Contains("$top=20&order=desc", source.Paths[1]);
        Assert.Contains("System.Description", source.RequestedFields);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("detail")]
    public async Task MissingTicketFailsBeforeAnyDiscussionRead(string view)
    {
        using var source = new LookupSourceHandler { ItemJson = """{"value":[]}""" };
        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Read(source, JsonSerializer.Serialize(new { id = 42, view })));
        Assert.Contains("not found or is not accessible", error.Message);
        Assert.Single(source.Paths);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DeniedTicketNeverReturnsOrLogsErrorBodyAsData(HttpStatusCode status)
    {
        using var source = new LookupSourceHandler { ItemStatus = status, ItemJson = "DENIED-TICKET-CONTENT" };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Read(source, """{"id":42}"""));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("Verify the id and your Azure DevOps access", error.Message);
        Assert.DoesNotContain("DENIED-TICKET-CONTENT", error.ToString());
        Assert.Single(source.Paths);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":[null]}""")]
    [InlineData("""{"value":[{"id":99,"rev":7,"fields":{"System.Title":"Denied title","System.State":"Active"}}]}""")]
    [InlineData("""{"value":[{"id":42,"fields":{"System.Title":"T","System.State":"Active"}}]}""")]
    [InlineData("""{"value":[{"id":42,"rev":0,"fields":{"System.Title":"T","System.State":"Active"}}]}""")]
    [InlineData("""{"value":[{"id":42,"rev":7,"fields":null}]}""")]
    [InlineData("""{"value":[{"id":42,"rev":7,"fields":{"System.Title":"T"}}]}""")]
    [InlineData("""{"value":[{"id":42,"rev":7,"fields":{"System.Title":"","System.State":"Active"}}]}""")]
    [InlineData("""{"value":[{"id":42,"rev":7,"fields":{"System.Title":"T","System.State":"Active","Microsoft.VSTS.Common.Priority":"1"}}]}""")]
    [InlineData("""{"value":[{"id":42,"rev":7,"fields":{"System.Title":"T","System.State":"Active","System.AssignedTo":{}}}]}""")]
    public async Task MalformedLookupNeverProducesNullFilledSuccess(string json)
    {
        using var source = new LookupSourceHandler { ItemJson = json };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(source, """{"id":42}"""));
        Assert.Contains("malformed work-item data", error.Message);
        Assert.DoesNotContain("Denied title", error.ToString());
        Assert.Single(source.Paths);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"id":0}""")]
    [InlineData("""{"id":-1}""")]
    [InlineData("""{"id":"42"}""")]
    [InlineData("""{"id":1.5}""")]
    [InlineData("""{"id":2147483648}""")]
    [InlineData("""{"id":42,"view":"all"}""")]
    [InlineData("""{"id":42,"view":null}""")]
    [InlineData("""{"id":42,"url":"https://untrusted.invalid"}""")]
    [InlineData("""{"id":42,"includeComments":true}""")]
    public async Task InvalidInputFailsBeforeAuthentication(string json)
    {
        var tool = new GetWorkItemTool((_, _) => throw new Exception("Authentication must not run"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            tool.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>(json), new AgentContext(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommentWindowsCarryUnknownOrKnownTotalsAndContinuation(bool knownTotal)
    {
        using var source = new LookupSourceHandler
        {
            LinkedCustomer = false, CommentTotal = knownTotal ? 100 : null, Continuation = true
        };
        var result = await Read(source, """{"id":42}""");
        var comments = result.GetProperty("coverage").GetProperty("comments");
        Assert.Equal(1, comments.GetProperty("returnedCount").GetInt32());
        Assert.Equal(1, comments.GetProperty("sourceReturnedCount").GetInt32());
        Assert.True(comments.GetProperty("isPartial").GetBoolean());
        Assert.Equal(knownTotal ? 100 : (int?)null, comments.GetProperty("totalCount").Deserialize<int?>());
        Assert.True(result.GetProperty("coverage").GetProperty("isPartial").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"comments":null}""")]
    [InlineData("""{"comments":[{}]}""")]
    [InlineData("""{"comments":[{"text":123}],"totalCount":1}""")]
    [InlineData("""{"comments":[{"text":"x"}],"totalCount":0}""")]
    public async Task MalformedDiscussionIsAnExplicitError(string json)
    {
        using var source = new LookupSourceHandler { CommentJson = json };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(source, """{"id":42}"""));
        Assert.Contains("malformed work-item discussion", error.Message);
        Assert.Equal(2, source.Paths.Count);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("null")]
    [InlineData("\"42\"")]
    [InlineData("42.0")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task SuppliedDiscussionOwnershipMustBeAnIntegerMatchingTheRequestedTicket(string owner)
    {
        using var source = new LookupSourceHandler
        {
            CommentJson = $$"""{"totalCount":1,"comments":[{"workItemId":{{owner}},"text":"Foreign discussion"}]}"""
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(source, """{"id":42}"""));
        Assert.Equal("Azure DevOps returned malformed work-item discussion data.", error.Message);
        Assert.DoesNotContain("Foreign discussion", error.ToString());
        Assert.Equal(2, source.Paths.Count);
    }

    [Theory]
    [InlineData("""{"workItemId":42,"commentId":7,"text":"Matching discussion"}""")]
    [InlineData("""{"workItemId":42,"id":7,"text":"Matching discussion"}""")]
    [InlineData("""{"id":7,"text":"Matching discussion"}""")]
    public async Task DiscussionSupportsMatchingOwnerAndOptionalIdentifiers(string comment)
    {
        using var source = new LookupSourceHandler { CommentJson = $$"""{"totalCount":1,"comments":[{{comment}}]}""" };
        var result = await Read(source, """{"id":42}""");
        AssertLookup(result);
        Assert.Equal("Matching discussion", Assert.Single(result.GetProperty("comments").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(3, source.Paths.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscussionFailuresAreNotSilentlyTurnedIntoEmptyDetails(bool customer)
    {
        using var source = new LookupSourceHandler
        {
            CommentStatus = customer ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            CustomerStatus = customer ? HttpStatusCode.Forbidden : HttpStatusCode.OK
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => Read(source, """{"id":42}"""));
        Assert.Equal(customer ? 3 : 2, source.Paths.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"children":null}""")]
    [InlineData("""{"children":[null]}""")]
    [InlineData("""{"children":[{}]}""")]
    [InlineData("""{"children":[{"text":123}]}""")]
    [InlineData("""{"children":[{"text":"Untrusted","author":"Not an identity"}]}""")]
    public async Task MalformedCustomerDiscussionIsAnExplicitSafeError(string json)
    {
        using var source = new LookupSourceHandler { CustomerJson = json };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(source, """{"id":42}"""));
        Assert.Equal("Customer discussion returned malformed data.", error.Message);
        Assert.Equal(3, source.Paths.Count);
    }

    [Fact]
    public async Task EmptyDiscussionHasKnownCompleteCoverage()
    {
        using var source = new LookupSourceHandler { LinkedCustomer = false, CommentCount = 0, CommentTotal = 0 };
        var result = await Read(source, """{"id":42}""");
        Assert.Empty(result.GetProperty("comments").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("customerComments").ValueKind);
        var coverage = result.GetProperty("coverage");
        Assert.False(coverage.GetProperty("isPartial").GetBoolean());
        Assert.False(coverage.GetProperty("outputTruncated").GetBoolean());
        Assert.Equal(0, coverage.GetProperty("comments").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task CustomerEntryCapIsReportedEvenWhenAllRetainedTextFits()
    {
        using var source = new LookupSourceHandler { CustomerCommentCount = 25 };
        var result = await Read(source, """{"id":42}""");
        Assert.Equal(20, result.GetProperty("customerComments").GetArrayLength());
        var coverage = result.GetProperty("coverage");
        Assert.True(coverage.GetProperty("outputTruncated").GetBoolean());
        Assert.Empty(coverage.GetProperty("truncatedFields").EnumerateArray());
        Assert.False(coverage.GetProperty("comments").GetProperty("isPartial").GetBoolean());
        Assert.Equal(25, coverage.GetProperty("customerComments").GetProperty("sourceReturnedCount").GetInt32());
        Assert.Equal(20, coverage.GetProperty("customerComments").GetProperty("returnedCount").GetInt32());
        Assert.True(coverage.GetProperty("customerComments").GetProperty("isPartial").GetBoolean());
    }

    [Fact]
    public async Task EscapedDetailsStayStructuredBoundedAndReportAllCoverageLimits()
    {
        using var source = new LookupSourceHandler
        {
            Description = new string('<', 10000), CommentText = new string('<', 2000),
            CommentCount = 25, CommentTotal = 100, CustomerCommentCount = 25, Continuation = true
        };
        var result = await Read(source, """{"id":42}""");
        AssertLookup(result);
        Assert.InRange(result.GetRawText().Length, 1, ReadResultBudget.MaxCharacters);
        Assert.False(result.TryGetProperty("preview", out _));
        var coverage = result.GetProperty("coverage");
        Assert.True(coverage.GetProperty("isPartial").GetBoolean());
        Assert.True(coverage.GetProperty("outputTruncated").GetBoolean());
        Assert.Contains("description", coverage.GetProperty("truncatedFields").EnumerateArray().Select(field => field.GetString()));
        foreach (var key in new[] { "comments", "customerComments" })
        {
            var retained = result.GetProperty(key).GetArrayLength();
            Assert.Equal(retained, coverage.GetProperty(key).GetProperty("returnedCount").GetInt32());
            Assert.Equal(25, coverage.GetProperty(key).GetProperty("sourceReturnedCount").GetInt32());
            Assert.True(coverage.GetProperty(key).GetProperty("isPartial").GetBoolean());
        }
        Assert.Equal(100, coverage.GetProperty("comments").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task LegacyWebServiceRetainsUnboundedDescriptionAndDetailedFields()
    {
        using var source = new LookupSourceHandler { Description = new string('x', 10000), CommentCount = 25, CommentTotal = 25 };
        var result = JsonSerializer.SerializeToElement(
            await new MonitorService("test-token", new HttpClient(source)).GetWorkItemDetails(42));
        Assert.Equal(source.Description, result.GetProperty("description").GetString());
        Assert.Equal(25, result.GetProperty("comments").GetArrayLength());
        Assert.Equal("engineer@example.com", result.GetProperty("assignedTo").GetProperty("uniqueName").GetString());
        Assert.DoesNotContain("$top", source.Paths[1]);
        AssertLookup(result);
    }

    [Fact]
    public async Task CancellationPreventsFurtherReads()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new LookupSourceHandler { OnRead = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tool(source).ExecuteAsync(JsonSerializer.SerializeToElement(new { id = 42 }),
                new AgentContext { DevOpsToken = "test-token" }, cancellation.Token));
        Assert.Single(source.Paths);
        var requests = source.Paths.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tool(source).ExecuteAsync(JsonSerializer.SerializeToElement(new { id = 42 }),
                new AgentContext(), cancellation.Token));
        Assert.Equal(requests, source.Paths.Count);
    }

    [Fact]
    public async Task CancellationDuringDiscussionPreventsCustomerRead()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        using var source = new LookupSourceHandler { OnRead = () => { if (++requests == 2) cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Tool(source).ExecuteAsync(JsonSerializer.SerializeToElement(new { id = 42 }),
                new AgentContext { DevOpsToken = "test-token" }, cancellation.Token));
        Assert.Equal(2, source.Paths.Count);
    }

    [Fact]
    public async Task InsufficientMetadataBudgetFailsExplicitlyInsteadOfReturningPreview()
    {
        using var source = new LookupSourceHandler();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Tool(source).ExecuteAsync(JsonSerializer.SerializeToElement(new { id = 42, view = "summary" }),
                new AgentContext { DevOpsToken = "test-token", ReadResultMaxCharacters = 128 }, default));
        Assert.Contains("metadata exceeds the remaining", error.Message);
    }

    [Fact]
    public async Task AgentRetainsStructuredLookupWithinRemainingAggregateBudget()
    {
        using var source = new LookupSourceHandler { Description = new string('<', 20000), CommentCount = 20, CommentTotal = 20 };
        var lookup = Tool(source);
        var model = new FakeAgentModel();
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse("", [
            new AgentToolCall("other", "other", "{}"),
            new AgentToolCall("lookup", lookup.Name, """{"id":42}""")
        ])));
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse("Read.", [])));
        var service = new AgentService(model, new AgentToolRegistry([
            new FakeTool("other", resultFactory: () => new { content = new string('x', 12000) }), lookup
        ]), new DenyWriteApprovalHandler(), NullLogger<AgentService>.Instance);
        var run = await service.RunAsync("Look up ticket 42", null, new AgentContext { DevOpsToken = "test-token" });
        var results = run.Transcript.Where(entry => entry.Role == "tool_result").ToArray();
        Assert.InRange(results.Sum(entry => entry.Content!.Length), 1, 16000);
        var result = JsonSerializer.Deserialize<JsonElement>(results[1].Content!);
        AssertLookup(result);
        Assert.False(result.TryGetProperty("preview", out _));
        Assert.True(result.GetProperty("coverage").GetProperty("isPartial").GetBoolean());
    }

    [Fact]
    public async Task ExhaustedAggregateBudgetSkipsLookupWithoutAuthenticationOrSourceReads()
    {
        var lookup = new GetWorkItemTool((_, _) => throw new Exception("Lookup must not execute"));
        var model = new FakeAgentModel();
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse("", [
            new AgentToolCall("first", "first", "{}"),
            new AgentToolCall("second", "second", "{}"),
            new AgentToolCall("lookup", lookup.Name, """{"id":42,"view":"summary"}""")
        ])));
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse("Read limit reached.", [])));
        var service = new AgentService(model, new AgentToolRegistry([
            new FakeTool("first", resultFactory: () => new { content = new string('x', 12000) }),
            new FakeTool("second", resultFactory: () => new { content = new string('x', 3600) }), lookup
        ]), new DenyWriteApprovalHandler(), NullLogger<AgentService>.Instance);
        var run = await service.RunAsync("Look up ticket 42", null, new AgentContext());
        var results = run.Transcript.Where(entry => entry.Role == "tool_result").ToArray();
        Assert.InRange(results.Sum(entry => entry.Content!.Length), 1, 16000);
        var result = JsonSerializer.Deserialize<JsonElement>(results[2].Content!);
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Contains("no read was performed", result.GetProperty("reason").GetString());
    }

    internal static GetWorkItemTool Tool(LookupSourceHandler source)
        => new(async (context, token) => new MonitorService(
            await context.GetRequiredDevOpsTokenAsync(token), new HttpClient(source)));

    private static async Task<JsonElement> Read(LookupSourceHandler source, string json)
    {
        var context = new AgentContext { DevOpsToken = "test-token", DryRun = false };
        var result = Assert.IsType<JsonElement>(await Tool(source).ExecuteAsync(
            JsonSerializer.Deserialize<JsonElement>(json), context, default));
        Assert.Empty(context.AuditLog);
        return result;
    }

    internal static void AssertLookup(JsonElement result, int id = 42)
    {
        Assert.Equal(id, result.GetProperty("id").GetInt32());
        Assert.Equal(7, result.GetProperty("revision").GetInt32());
        Assert.Equal("Original title", result.GetProperty("title").GetString());
        Assert.Equal("Active", result.GetProperty("state").GetString());
        Assert.Equal(2, result.GetProperty("priority").GetInt32());
        Assert.Equal($"https://dev.azure.com/devdiv/devdiv/_workitems/edit/{id}", result.GetProperty("url").GetString());
    }
}

internal sealed class LookupSourceHandler : HttpMessageHandler
{
    public int Id { get; init; } = 42;
    public string? ItemJson { get; set; }
    public string? CommentJson { get; init; }
    public string? CustomerJson { get; init; }
    public HttpStatusCode ItemStatus { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode CommentStatus { get; init; } = HttpStatusCode.OK;
    public HttpStatusCode CustomerStatus { get; init; } = HttpStatusCode.OK;
    public string Description { get; init; } = "<p>Details</p>";
    public string CommentText { get; init; } = "Internal comment";
    public int CommentCount { get; init; } = 1;
    public int? CommentTotal { get; init; } = 1;
    public int CustomerCommentCount { get; init; } = 1;
    public bool LinkedCustomer { get; init; } = true;
    public bool Continuation { get; init; }
    public bool Assigned { get; init; } = true;
    public string? DisplayName { get; init; } = "Engineer";
    public int? Priority { get; init; } = 2;
    public Action? OnRead { get; init; }
    public List<string> Paths { get; } = [];
    public string[] RequestedFields { get; private set; } = [];
    public List<string?> Authorization { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri!.PathAndQuery);
        Authorization.Add(request.Headers.Authorization?.ToString());
        OnRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        HttpStatusCode status;
        string json;
        if (request.RequestUri.AbsolutePath.EndsWith("/workitemsbatch", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(Id, Assert.Single(body.RootElement.GetProperty("ids").EnumerateArray()).GetInt32());
            RequestedFields = body.RootElement.GetProperty("fields").EnumerateArray().Select(field => field.GetString()!).ToArray();
            status = ItemStatus;
            json = ItemJson ?? JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    new
                    {
                        id = Id, rev = 7, url = "https://untrusted.invalid/never-fetch",
                        fields = new Dictionary<string, object?>
                        {
                            ["System.Title"] = "Original title", ["System.State"] = "Active",
                            ["System.Description"] = Description, ["Microsoft.VSTS.Common.Priority"] = Priority,
                            ["System.AssignedTo"] = Assigned ? new { displayName = DisplayName, uniqueName = "engineer@example.com" } : null,
                            ["System.Tags"] = "one; two", ["Microsoft.DevDiv.DeveloperCommunityId"] = LinkedCustomer ? "123" : null
                        }
                    }
                }
            });
        }
        else if (request.RequestUri.AbsolutePath.EndsWith("/comments", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            status = CommentStatus;
            var comments = new Dictionary<string, object?>
            {
                ["comments"] = Enumerable.Range(1, CommentCount).Select(id => new
                {
                    workItemId = Id, commentId = id, text = CommentText, modifiedDate = "2026-11-01T00:00:00Z"
                }),
                ["continuationToken"] = Continuation ? "next-page" : null
            };
            if (CommentTotal.HasValue) comments["totalCount"] = CommentTotal;
            json = CommentJson ?? JsonSerializer.Serialize(comments);
        }
        else
        {
            Assert.Equal("sendvsfeedback2.azurewebsites.net", request.RequestUri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
            status = CustomerStatus;
            json = CustomerJson ?? JsonSerializer.Serialize(new
            {
                id = 123, title = "Customer post", content = "Customer content",
                children = Enumerable.Range(1, CustomerCommentCount).Select(id => new
                {
                    id, text = "Customer comment", description = "More customer context",
                    author = new { name = "Customer" }
                })
            });
        }
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
