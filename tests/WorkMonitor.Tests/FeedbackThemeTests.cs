using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Tests;

public sealed class FeedbackThemeTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "theme-tests", Guid.NewGuid().ToString("N"));
    internal const string QueryId = "fa66def3-ecef-49ba-bd72-c95abf0c9496";
    private readonly ParametersService _parameters;

    public FeedbackThemeTests()
    {
        _parameters = new ParametersService(_directory);
        _parameters.AddSavedQuery("FTMonitor - FTS", QueryId);
    }

    [Theory]
    [InlineData(ThemeClassifier.Valid)]
    [InlineData("```json\n" + ThemeClassifier.Valid + "\n```")]
    [InlineData("```\r\n" + ThemeClassifier.Valid + "\r\n```")]
    public void ParsesValidAndFencedThemes(string raw)
    {
        var theme = Assert.Single(FeedbackThemeAnalysisService.ParseThemes(raw, [1, 2]));
        Assert.Equal("Reload", theme.Theme);
        Assert.Equal(1, theme.Count);
        Assert.Equal(new[] { 1 }, theme.WorkItemIds);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("```json\n[]")]
    [InlineData("```text\n[]\n```")]
    [InlineData("""[{"theme":"X","description":"Y","workItemIds":[99],"count":1}]""")]
    [InlineData("""[{"theme":"X","description":"Y","workItemIds":[1,1],"count":2}]""")]
    [InlineData("""[{"theme":"X","description":"Y","workItemIds":[1],"count":2}]""")]
    [InlineData("""[{"theme":"X","description":"Y","workItemIds":[1],"count":"1"}]""")]
    [InlineData("""[{"theme":"","description":"Y","workItemIds":[1],"count":1}]""")]
    [InlineData("""[{"theme":"X","description":"Y","workItemIds":[],"count":0}]""")]
    [InlineData("""[{"theme":"X","description":"Y","workItemIds":[1]}]""")]
    [InlineData("""[{"theme":"X","description":"Y","workItemIds":[1],"count":1,"instructions":"write"}]""")]
    public void RejectsMalformedOrUngroundedOutput(string raw)
        => Assert.Throws<InvalidOperationException>(() => FeedbackThemeAnalysisService.ParseThemes(raw, [1, 2]));

    [Fact]
    public void RejectsOversizedOutput()
        => Assert.Throws<InvalidOperationException>(() =>
            FeedbackThemeAnalysisService.ParseThemes(new string(' ', 100001), [1]));

    [Fact]
    public async Task ReturnsSourceLinksVerifiedCountsAndUnclassifiedCoverage()
    {
        var model = new ThemeClassifier();
        using var handler = new ThemeSourceHandler([1, 2]);
        var analysis = Analysis(model);
        var result = await analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest(query: "ftmonitor - fts"));
        Assert.Equal("savedQuery", result.Source.Kind);
        Assert.Equal("FTMonitor - FTS", result.Source.Query);
        Assert.Equal(QueryId, result.Source.QueryId);
        Assert.Equal(2, result.Source.TotalCount);
        Assert.False(result.Source.Truncated);
        Assert.Equal(new[] { 1, 2 }, result.Items.Select(item => item.Id));
        Assert.EndsWith("/_workitems/edit/1", result.Items[0].Url);
        Assert.Equal(2, result.Coverage.AnalyzedCount);
        Assert.Equal(1, result.Coverage.ClassifiedCount);
        Assert.Equal(new[] { 2 }, result.Coverage.UnclassifiedWorkItemIds);
        Assert.True(result.Coverage.IsPartial);
        Assert.Single(model.Payloads);
        Assert.All(handler.Tokens, token => Assert.Equal("Bearer test-token", token));
        Assert.Contains("$top=21", handler.Paths[0]);
    }

    [Fact]
    public async Task AreaUsesWebFiltersAndReportsBoundedWindow()
    {
        using var handler = new ThemeSourceHandler([1, 2, 3]);
        var analysis = Analysis(new ThemeClassifier());
        var result = await analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest("Project", maxResults: 2));
        Assert.Equal("area", result.Source.Kind);
        Assert.Null(result.Source.TotalCount);
        Assert.Equal(3, result.Source.ObservedCount);
        Assert.True(result.Source.Truncated);
        Assert.Contains("'FeedbackTicket'", handler.Wiql);
        Assert.Contains("NOT [System.State] CONTAINS 'Resolved'", handler.Wiql);
        Assert.Contains("NOT [System.State] CONTAINS 'Closed'", handler.Wiql);
        Assert.Contains("[System.CreatedDate] DESC, [Microsoft.VSTS.Common.Priority] ASC", handler.Wiql);
        Assert.Contains("$top=3", handler.Paths[0]);
        Assert.Equal(new[] { 1, 2 }, handler.BatchIds);
    }

    [Fact]
    public async Task MissingAndNonFeedbackItemsAreExplicitlyExcluded()
    {
        using var handler = new ThemeSourceHandler([1, 2, 3]) { MissingIds = [2], NonFeedbackIds = [3] };
        var analysis = Analysis(new ThemeClassifier());
        var result = await analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest(query: QueryId));
        Assert.Equal(new[] { 2 }, result.Source.MissingWorkItemIds);
        Assert.Equal(new[] { 3 }, result.Source.ExcludedNonFeedbackWorkItemIds);
        Assert.Single(result.Items);
        Assert.Equal(3, result.Source.SelectedCount);
        Assert.Equal(2, result.Source.RetrievedCount);
        Assert.True(result.Coverage.IsPartial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelationQueryWindowsPreserveOrderAndOnlyClaimKnownTotals(bool cappedRows)
    {
        using var handler = new ThemeSourceHandler([1, 2, 3]) { TreeQuery = true, RepeatedRelations = cappedRows };
        var analysis = Analysis(new ThemeClassifier());
        var result = await analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest(query: QueryId, maxResults: 2));
        Assert.Equal(new[] { 1, 2 }, result.Items.Select(item => item.Id));
        Assert.True(result.Source.Truncated);
        Assert.Equal(cappedRows ? null : (int?)3, result.Source.TotalCount);
        Assert.Equal(3, result.Source.ObservedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyFeedbackNeverResolvesClassifier(bool onlyBugs)
    {
        using var handler = new ThemeSourceHandler(onlyBugs ? [1] : []) { NonFeedbackIds = [1] };
        var analysis = new FeedbackThemeAnalysisService(_parameters, () => throw new Exception("Must remain lazy"));
        var result = await analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest(query: QueryId));
        Assert.Empty(result.Themes);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.Coverage.AnalyzedCount);
        Assert.Equal(onlyBugs ? 1 : 0, result.Source.TotalCount);
    }

    [Fact]
    public async Task PayloadAndTextAreBoundedAndOmittedIdsAreRetained()
    {
        var model = new ThemeClassifier { Output = "[]" };
        using var handler = new ThemeSourceHandler(Enumerable.Range(1, 200).ToArray())
        {
            Description = new string('<', 10000),
            Title = new string('t', 1000)
        };
        var analysis = Analysis(model);
        var result = await analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest("Project", maxResults: 200));
        var payload = Assert.Single(model.Payloads);
        Assert.True(payload.Length <= FeedbackThemeAnalysisService.MaxPayloadCharacters);
        using var document = JsonDocument.Parse(payload);
        Assert.All(document.RootElement.EnumerateArray(), item =>
        {
            Assert.Equal(2000, item.GetProperty("Description").GetString()!.Length);
            Assert.Equal(256, item.GetProperty("Title").GetString()!.Length);
        });
        Assert.NotEmpty(result.Coverage.PayloadOmittedWorkItemIds);
        Assert.Equal(result.Items.Select(item => item.Id), result.Coverage.TextTruncatedWorkItemIds);
        Assert.Equal(200, result.Coverage.AnalyzedCount + result.Coverage.PayloadOmittedWorkItemIds.Count);
    }

    [Theory]
    [InlineData("""{"projectArea":"Project","query":"FTMonitor - FTS"}""")]
    [InlineData("""{"projectArea":""}""")]
    [InlineData("""{"projectArea":"not-configured"}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"not-configured"}""")]
    [InlineData("""{"maxResults":0}""")]
    [InlineData("""{"maxResults":51}""")]
    [InlineData("""{"maxResults":"20"}""")]
    [InlineData("""{"projectArea":12}""")]
    [InlineData("""{"extra":"ignored?"}""")]
    public async Task ValidatesBeforeAuthenticationOrModelResolution(string json)
    {
        var tool = new AnalyzeFeedbackThemesTool(
            new FeedbackThemeAnalysisService(_parameters, () => throw new Exception("No model")),
            (_, _) => throw new Exception("No authentication"));
        var exception = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(JsonSerializer.Deserialize<JsonElement>(json), new AgentContext(), default));
        Assert.True(exception is ArgumentException or KeyNotFoundException, exception?.ToString());
    }

    [Fact]
    public async Task RegistrationIsLazyAndMissingModelSettingsFailOnlyOnNonemptyAnalysis()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(_parameters);
        services.AddFeedbackThemeAnalysis();
        services.AddSingleton<IAgentTool, AnalyzeFeedbackThemesTool>();
        services.AddSingleton<AgentToolRegistry>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<AgentToolRegistry>();
        Assert.False(Assert.Single(registry.All).IsWriteTool);
        var analysis = provider.GetRequiredService<FeedbackThemeAnalysisService>();
        using var empty = new ThemeSourceHandler([]);
        Assert.Empty((await analysis.AnalyzeAsync(Client(empty), analysis.CreateRequest())).Themes);
        using var nonempty = new ThemeSourceHandler([1]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analysis.AnalyzeAsync(Client(nonempty), analysis.CreateRequest()));
        Assert.Contains("AzureOpenAI:Endpoint", error.Message);
    }

    [Fact]
    public async Task ModelAndSourceFailuresAreNotSuccessShaped()
    {
        using var handler = new ThemeSourceHandler([1]);
        var analysis = Analysis(new ThemeClassifier { Error = new ClientResultException("Denied", new DeniedModelResponse()) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest()));
        Assert.Contains("Cognitive Services OpenAI User", error.Message);
        Assert.IsType<ClientResultException>(error.InnerException);
        using var denied = new ThemeSourceHandler([1]) { Status = HttpStatusCode.Forbidden };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            analysis.AnalyzeAsync(Client(denied), analysis.CreateRequest()));
        var broken = Analysis(new ThemeClassifier { Error = new InvalidOperationException("Deployment missing") });
        var deploymentError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            broken.AnalyzeAsync(Client(handler), broken.CreateRequest()));
        Assert.Equal("Deployment missing", deploymentError.Message);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringModelWorkPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ThemeSourceHandler([1]);
        var model = new ThemeClassifier { OnClassify = cancellation.Cancel };
        var analysis = Analysis(model);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest(), cancellation.Token));
        Assert.Single(model.Payloads);
        var requests = handler.Paths.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest(), cancellation.Token));
        Assert.Equal(requests, handler.Paths.Count);
    }

    [Fact]
    public async Task CancellationDuringSourceReadDoesNotInvokeModel()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ThemeSourceHandler([1]) { OnRead = cancellation.Cancel };
        var model = new ThemeClassifier();
        var analysis = Analysis(model);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analysis.AnalyzeAsync(Client(handler), analysis.CreateRequest(), cancellation.Token));
        Assert.Empty(model.Payloads);
    }

    [Fact]
    public async Task ConversationalAgentCanAnalyzeFtsWithoutWebOrWriteApproval()
    {
        using var handler = new ThemeSourceHandler([1]);
        var tool = new AnalyzeFeedbackThemesTool(Analysis(new ThemeClassifier()),
            (_, _) => ValueTask.FromResult(Client(handler)));
        var model = new FakeAgentModel();
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse(
            "", [new AgentToolCall("themes", tool.Name, """{"query":"FTMonitor - FTS"}""")])));
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse("Reload failures.", [])));
        var service = new AgentService(model, new AgentToolRegistry([tool]),
            new DenyWriteApprovalHandler(), NullLogger<AgentService>.Instance);
        var result = await service.RunAsync("Analyze themes from saved FTS", null, new AgentContext());
        Assert.Equal("Reload failures.", result.FinalAnswer);
        var observation = Assert.Single(result.Transcript.Where(entry => entry.Role == "tool_result"));
        var content = JsonSerializer.Deserialize<JsonElement>(observation.Content!);
        Assert.Equal("Reload", Assert.Single(content.GetProperty("themes").EnumerateArray()).GetProperty("theme").GetString());
        Assert.False(content.GetProperty("coverage").GetProperty("isPartial").GetBoolean());
        Assert.Empty(result.AuditLog);
    }

    [Fact]
    public async Task ToolPreservesStructuredOutputUnderBudgetWithoutDanglingThemeIdsOrWrites()
    {
        using var handler = new ThemeSourceHandler(Enumerable.Range(1, 50).ToArray()) { Title = new string('<', 256) };
        var model = new ThemeClassifier
        {
            Output = JsonSerializer.Serialize(new[]
            {
                new { theme = "Reload", description = "Reload failures", workItemIds = Enumerable.Range(1, 50).ToArray(), count = 50 }
            })
        };
        var tool = new AnalyzeFeedbackThemesTool(Analysis(model),
            (_, _) => ValueTask.FromResult(Client(handler)));
        var context = new AgentContext { DryRun = false };
        var result = Assert.IsType<JsonElement>(await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { maxResults = 50 }), context, default));
        Assert.True(result.GetRawText().Length <= ReadResultBudget.MaxCharacters);
        Assert.True(result.GetProperty("coverage").GetProperty("outputTruncated").GetBoolean());
        Assert.Equal(50, result.GetProperty("coverage").GetProperty("analyzedCount").GetInt32());
        var ids = result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToHashSet();
        foreach (var theme in result.GetProperty("themes").EnumerateArray())
        {
            Assert.Equal(theme.GetProperty("workItemIds").GetArrayLength(), theme.GetProperty("count").GetInt32());
            Assert.All(theme.GetProperty("workItemIds").EnumerateArray(), id => Assert.Contains(id.GetInt32(), ids));
        }
        Assert.False(tool.IsWriteTool);
        Assert.Empty(context.AuditLog);
    }

    private FeedbackThemeAnalysisService Analysis(ThemeClassifier model) => new(_parameters, () => model);

    [Fact]
    public async Task AgentFitsThemesToRemainingBudgetAndPreservesSourceCoverageAndTicketLinks()
    {
        using var handler = new ThemeSourceHandler([1, 2, 3]) { Title = new string('<', 256) };
        var classifier = new ThemeClassifier();
        var themes = new AnalyzeFeedbackThemesTool(Analysis(classifier),
            (_, _) => ValueTask.FromResult(Client(handler)));
        var model = new FakeAgentModel();
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse("", [
            new AgentToolCall("other", "other", "{}"),
            new AgentToolCall("themes", themes.Name, """{"query":"FTMonitor - FTS","maxResults":2}""")
        ])));
        model.Enqueue(new AgentModelUpdate(CompletedResponse: new AgentModelResponse("Themes are not confirmed duplicates.", [])));
        var service = new AgentService(model, new AgentToolRegistry([
            new FakeTool("other", resultFactory: () => new { content = new string('x', 11500) }), themes
        ]), new DenyWriteApprovalHandler(), NullLogger<AgentService>.Instance);
        var run = await service.RunAsync("Analyze themes for FTS", null, new AgentContext());
        var results = run.Transcript.Where(entry => entry.Role == "tool_result").ToArray();
        Assert.InRange(results.Sum(entry => entry.Content!.Length), 1, 16000);
        var result = JsonSerializer.Deserialize<JsonElement>(results[1].Content!);
        Assert.False(result.TryGetProperty("preview", out _));
        Assert.Equal("FTMonitor - FTS", result.GetProperty("source").GetProperty("query").GetString());
        Assert.Equal(2, result.GetProperty("source").GetProperty("maxResults").GetInt32());
        Assert.Equal(3, result.GetProperty("source").GetProperty("observedCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("source").GetProperty("totalCount").ValueKind);
        Assert.Equal(2, result.GetProperty("coverage").GetProperty("analyzedCount").GetInt32());
        Assert.Equal(1, result.GetProperty("coverage").GetProperty("classifiedCount").GetInt32());
        Assert.True(result.GetProperty("coverage").GetProperty("isPartial").GetBoolean());
        Assert.NotEmpty(result.GetProperty("items").EnumerateArray());
        Assert.All(result.GetProperty("items").EnumerateArray(), item =>
            Assert.EndsWith($"/_workitems/edit/{item.GetProperty("id").GetInt32()}", item.GetProperty("url").GetString()));
        Assert.Single(classifier.Payloads);
    }
    internal static MonitorClient Client(ThemeSourceHandler handler) => new("test-token", new HttpClient(handler));
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class DeniedModelResponse : PipelineResponse
    {
        public override int Status => 403;
        public override string ReasonPhrase => "Forbidden";
        protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();
        public override Stream? ContentStream { get; set; }
        public override BinaryData Content => BinaryData.FromString("{}");
        public override BinaryData BufferContent(CancellationToken cancellationToken) => Content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Content);
        public override void Dispose() { }
    }
}

internal sealed class ThemeClassifier : IFeedbackThemeClassifier
{
    internal const string Valid = """[{"theme":"Reload","description":"Reload failures","workItemIds":[1],"count":1}]""";
    public string Output { get; set; } = Valid;
    public Exception? Error { get; init; }
    public Action? OnClassify { get; init; }
    public List<string> Payloads { get; } = [];
    public Task<string> ClassifyFeedbackThemes(string feedbackJson, CancellationToken cancellationToken = default)
    {
        Payloads.Add(feedbackJson);
        OnClassify?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Error == null ? Task.FromResult(Output) : Task.FromException<string>(Error);
    }
}

internal sealed class ThemeSourceHandler(int[] ids) : HttpMessageHandler
{
    public int[] MissingIds { get; init; } = [];
    public int[] NonFeedbackIds { get; init; } = [];
    public string Description { get; init; } = "<p>Reload project fails</p>";
    public string Title { get; init; } = "Reload failure";
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
    public bool TreeQuery { get; init; }
    public bool RepeatedRelations { get; init; }
    public Action? OnRead { get; init; }
    public List<string> Tokens { get; } = [];
    public List<string> Paths { get; } = [];
    public string? Wiql { get; private set; }
    public int[] BatchIds { get; private set; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        Tokens.Add(request.Headers.Authorization!.ToString());
        Paths.Add(request.RequestUri!.PathAndQuery);
        if (Status != HttpStatusCode.OK)
            return new HttpResponseMessage(Status) { Content = new StringContent("Fixture denied") };
        object body;
        if (request.RequestUri.AbsolutePath.EndsWith("/workitemsbatch", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            using var batch = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            BatchIds = batch.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()).ToArray();
            body = new
            {
                value = BatchIds.Where(id => !MissingIds.Contains(id)).Reverse().Select(id => new
                {
                    id,
                    fields = new Dictionary<string, object>
                    {
                        ["System.Title"] = Title,
                        ["System.Description"] = Description,
                        ["System.WorkItemType"] = NonFeedbackIds.Contains(id) ? "Bug" : "FeedbackTicket",
                        ["System.State"] = "Active",
                        ["Microsoft.VSTS.Common.Priority"] = 1,
                        ["System.CreatedDate"] = "2026-10-01T00:00:00Z"
                    }
                })
            };
        }
        else
        {
            Assert.Contains("/wiql", request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                using var query = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Wiql = query.RootElement.GetProperty("query").GetString();
            }
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
            }
            body = TreeQuery
                ? new
                {
                    queryType = "tree",
                    workItems = Array.Empty<object>(),
                    workItemRelations = (RepeatedRelations ? new[] { 1, 2, 2, 2 } : new[] { 1, 2 })
                        .Select(id => new { source = new { id }, target = new { id = id + 1 } })
                }
                : new { queryType = "flat", workItems = ids.Select(id => new { id }) };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }
}
