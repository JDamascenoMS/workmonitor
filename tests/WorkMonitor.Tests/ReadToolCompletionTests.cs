using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Models;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Tests;

public sealed class ReadToolCompletionTests
{
    [Fact]
    public async Task MentionActivityExcludesClosedAndOldChangedItems()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new ResponseHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/wiql"))
            {
                return Task.FromResult(JsonResponse(new { workItems = Array.Empty<object>() }));
            }

            return Task.FromResult(JsonResponse(new
            {
                value = new[]
                {
                    WorkItem(1, "Active", now),
                    WorkItem(2, "Closed - Duplicate", now),
                    WorkItem(3, "Active", now.AddDays(-30))
                }
            }));
        });
        var client = new MonitorClient(
            "test-token",
            new HttpClient(handler),
            new MentionsProvider(
                new RecentMentionReference(1, now),
                new RecentMentionReference(2, now),
                new RecentMentionReference(3, now)));

        var result = await client.GetMyRecentActivity(
            days: 7,
            maxResults: 5,
            commentsPerItem: 0);

        Assert.Equal(1, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task ActivityCandidateBatchNeverExceedsOneHundredIds()
    {
        var now = DateTimeOffset.UtcNow;
        var batchSize = 0;
        var handler = new ResponseHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/wiql"))
            {
                return JsonResponse(new
                {
                    workItems = Enumerable.Range(1, 50).Select(id => new { id })
                });
            }

            using var body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(token));
            batchSize = body.RootElement.GetProperty("ids").GetArrayLength();
            return JsonResponse(new
            {
                value = new[] { WorkItem(1, "Active", now) }
            });
        });
        var client = new MonitorClient(
            "test-token",
            new HttpClient(handler),
            new MentionsProvider(
                Enumerable.Range(100, 500)
                    .Select(id => new RecentMentionReference(id, now))
                    .ToArray()));

        var result = await client.GetMyRecentActivity(commentsPerItem: 0);

        Assert.Equal(100, batchSize);
        Assert.True(result.CandidateWindowTruncated);
    }

    [Fact]
    public void OutputBudgetCountsJsonEscapingAndPreservesStructuredResults()
    {
        var items = Enumerable.Range(1, 10)
            .Select(id => new RecentWorkItemActivity(
                id, "title", "Bug", "Active", 1, "area", "user", DateTimeOffset.UtcNow,
                new[]
                {
                    new RecentCommentActivity(1, new string('<', 500), DateTimeOffset.UtcNow),
                    new RecentCommentActivity(2, new string('<', 500), DateTimeOffset.UtcNow)
                }))
            .ToArray();

        var result = ReadResultBudget.FitCollection<
            RecentWorkItemActivity, RecentActivityResult>(
            items,
            (retained, truncated) => new RecentActivityResult(10, false, retained, truncated));

        Assert.True(result.ResultTruncated);
        Assert.NotEmpty(result.Items);
        Assert.InRange(JsonSerializer.Serialize(result).Length, 1, ReadResultBudget.MaxCharacters);
    }

    [Fact]
    public async Task CorrelationFillsResultSlotsAfterStaleReferences()
    {
        var tool = CorrelationTool(new[] { 1, 2 }, issueNumber =>
            issueNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(new
                {
                    number = 2, title = "Valid reference", body = "Evidence", comments = 0
                }));
        var arguments = JsonSerializer.SerializeToElement(new { workItemId = 42, maxResults = 1 });

        var result = JsonSerializer.SerializeToElement(
            await tool.ExecuteAsync(arguments, new AgentContext(), CancellationToken.None));

        var correlation = Assert.Single(
            result.GetProperty("correlations").EnumerateArray().ToArray());
        Assert.Equal(2, correlation.GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task CorrelationPreservesShapeUnderMaximumEscapedEvidence()
    {
        var tool = CorrelationTool(Enumerable.Range(1, 5).ToArray(), issueNumber =>
            JsonResponse(new
            {
                number = issueNumber,
                title = new string('<', 300),
                body = new string('<', 1000),
                comments = 1
            }));
        var arguments = JsonSerializer.SerializeToElement(new { workItemId = 42, maxResults = 5 });

        var result = await tool.ExecuteAsync(arguments, new AgentContext(), CancellationToken.None);
        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);

        Assert.InRange(json.Length, 1, ReadResultBudget.MaxCharacters);
        Assert.True(document.RootElement.GetProperty("resultTruncated").GetBoolean());
        Assert.NotEmpty(document.RootElement.GetProperty("correlations").EnumerateArray().ToArray());
    }

    [Theory]
    [InlineData("https://dev.azure.com/other-org/project/_workitems/edit/42", 0)]
    [InlineData("https://dev.azure.com/devdiv/devdiv/_workitems/edit/42", 1)]
    public async Task ReverseSearchHitsRequireVerifiedDevDivEvidence(
        string reference,
        int expectedCount)
    {
        var tool = CorrelationTool(Array.Empty<int>(), _ =>
            JsonResponse(new
            {
                number = 9, title = "Search hit", body = reference, comments = 0
            }),
            reverseSearch: true);
        var arguments = JsonSerializer.SerializeToElement(new { workItemId = 42, maxResults = 1 });

        var result = JsonSerializer.SerializeToElement(
            await tool.ExecuteAsync(arguments, new AgentContext(), CancellationToken.None));

        Assert.Equal(
            expectedCount,
            result.GetProperty("correlations").GetArrayLength());
    }

    private static CorrelateWorkItemWithGitHubTool CorrelationTool(
        IReadOnlyList<int> references,
        Func<int, HttpResponseMessage> issueResponse,
        bool reverseSearch = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Repositories:0:name"] = "test",
                ["GitHub:Repositories:0:owner"] = "owner",
                ["GitHub:Repositories:0:repo"] = "repo"
            })
            .Build();
        var description = string.Join(
            " ", references.Select(number => $"https://github.com/owner/repo/issues/{number}"));
        var devOps = new MonitorClient(
            "test-token",
            new HttpClient(new ResponseHandler((request, _) =>
                Task.FromResult(request.Method == HttpMethod.Post
                    ? JsonResponse(new
                    {
                        value = new[] { WorkItem(42, "Active", DateTimeOffset.UtcNow, description) }
                    })
                    : JsonResponse(new { totalCount = 0, comments = Array.Empty<object>() })))));
        var github = new GitHubClient(
            new HttpClient(new ResponseHandler((request, _) =>
            {
                var uri = request.RequestUri!;
                if (uri.AbsolutePath.EndsWith("/search/issues"))
                {
                    return Task.FromResult(JsonResponse(new
                    {
                        total_count = reverseSearch ? 1 : 0,
                        items = reverseSearch
                            ? new[] { new { number = 9 } }
                            : Array.Empty<object>()
                    }));
                }

                if (uri.AbsolutePath.EndsWith("/comments"))
                {
                    return Task.FromResult(JsonResponse(new[]
                    {
                        new { body = new string('<', 500), created_at = DateTimeOffset.UtcNow }
                    }));
                }

                return Task.FromResult(issueResponse(
                    int.Parse(uri.Segments.Last())));
            })),
            configuration);
        return new CorrelateWorkItemWithGitHubTool(
            github, configuration, (_, _) => ValueTask.FromResult(devOps));
    }

    private static object WorkItem(
        int id, string state, DateTimeOffset changedAt, string description = "")
        => new
        {
            id,
            fields = new Dictionary<string, object?>
            {
                ["System.Title"] = "Test item",
                ["System.WorkItemType"] = "Bug",
                ["System.State"] = state,
                ["System.ChangedDate"] = changedAt.ToString("O"),
                ["System.Description"] = description
            }
        };

    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };

    private sealed class MentionsProvider(params RecentMentionReference[] mentions)
        : IRecentMentionsProvider
    {
        public Task<IReadOnlyList<RecentMentionReference>> GetRecentMentionsAsync(
            string accessToken, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RecentMentionReference>>(mentions);
    }

    private sealed class ResponseHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
