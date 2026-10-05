using System.Net;
using System.Text;
using System.Text.Json;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Models;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Tests;

public sealed class MonitorClientTests
{
    [Fact]
    public async Task MyAssignedItemsUsesAuthenticatedUserMacro()
    {
        var handler = new CaptureHandler();
        var client = new MonitorClient(
            "test-token",
            new HttpClient(handler));

        var result = await client.GetMyAssignedItems(
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Contains(
            "[System.AssignedTo] = @Me",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'@Me'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Closed'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Resolved'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Done'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Removed'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Completed'",
            handler.RequestBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MentionedItemsUsesIdentityAwareProvider()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new MentionHandler()),
            new FakeRecentMentionsProvider(
                new RecentMentionReference(
                    42,
                    DateTimeOffset.Parse("2026-09-10T00:00:00Z"))));

        var result = await client.GetMyMentionedItems(
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(42, item.id);
    }

    [Fact]
    public async Task SavedQueryPreservesOrderAndBoundsResults()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new SavedQueryHandler(treeQuery: false)));

        var (items, totalCount) = await client.GetWorkItemsFromSavedQuery(
            "fa66def3-ecef-49ba-bd72-c95abf0c9496",
            maxResults: 2,
            CancellationToken.None);

        Assert.Equal(3, totalCount);
        Assert.Equal(new[] { 3, 1 }, items.Select(item => item.id));
    }

    [Fact]
    public async Task SavedTreeQueryExtractsRelationIds()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new SavedQueryHandler(treeQuery: true)));

        var (items, totalCount) = await client.GetWorkItemsFromSavedQuery(
            "fa66def3-ecef-49ba-bd72-c95abf0c9496",
            maxResults: 3,
            CancellationToken.None);

        Assert.Equal(3, totalCount);
        Assert.Equal(new[] { 3, 1, 2 }, items.Select(item => item.id));
    }

    [Fact]
    public async Task EmptyTreeQueryReturnsNoItems()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new EmptyTreeQueryHandler()));

        var (items, totalCount) = await client.GetWorkItemsFromSavedQuery(
            "fa66def3-ecef-49ba-bd72-c95abf0c9496",
            maxResults: 3,
            CancellationToken.None);

        Assert.Empty(items);
        Assert.Equal(0, totalCount);
    }

    [Fact]
    public async Task PullRequestReviewUsesAuthenticatedIdentityAndPendingVote()
    {
        var handler = new PullRequestHandler();
        var client = new MonitorClient(
            "test-token",
            new HttpClient(handler));

        var pullRequests = await client.GetPullRequestsRequiringMyReview(
            maxResults: 10,
            CancellationToken.None);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal(101, pullRequest.PullRequestId);
        Assert.Equal("repo one", pullRequest.Repository);
        Assert.Equal("feature", pullRequest.SourceBranch);
        Assert.Equal("Author", pullRequest.Author);
        Assert.Contains(
            "searchCriteria.reviewerId=user-1",
            handler.PullRequestQuery,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullRequestReviewPaginatesPastAlreadyVotedPage()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new PagedPullRequestHandler()));

        var pullRequests = await client.GetPullRequestsRequiringMyReview(
            maxResults: 1,
            CancellationToken.None);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal(999, pullRequest.PullRequestId);
    }

    [Fact]
    public async Task RecentActivityReturnsNewestBoundedComments()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new RecentActivityHandler()),
            new FakeRecentMentionsProvider(
                new RecentMentionReference(
                    42,
                    DateTimeOffset.UtcNow)));

        var activity = await client.GetMyRecentActivity(
            days: 7,
            maxResults: 1,
            commentsPerItem: 2,
            CancellationToken.None);

        var item = Assert.Single(activity.Items);
        Assert.Equal(42, item.Id);
        Assert.Equal(
            new[] { 3, 2 },
            item.RecentComments.Select(comment => comment.CommentId));
    }

    [Fact]
    public async Task RecentActivityExcludesMentionsOutsideLookback()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new EmptyActivityHandler()),
            new FakeRecentMentionsProvider(
                new RecentMentionReference(
                    99,
                    DateTimeOffset.UtcNow.AddDays(-30))));

        var activity = await client.GetMyRecentActivity(
            days: 7,
            maxResults: 5,
            commentsPerItem: 0,
            CancellationToken.None);

        Assert.Empty(activity.Items);
        Assert.Equal(0, activity.CandidateCount);
    }

    [Fact]
    public async Task CommentScanningFollowsContinuationAndReportsPartial()
    {
        var client = new MonitorClient(
            "test-token",
            new HttpClient(new CommentPagingHandler()));

        var result = await client.GetCommentsForScanning(
            42,
            maxComments: 3,
            CancellationToken.None);

        Assert.Equal(5, result.TotalCount);
        Assert.True(result.IsPartial);
        Assert.Equal(new[] { 1, 2, 3 }, result.Comments.Select(comment => comment.commentId));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"workItems":[]}""");
        }
    }

    private sealed class MentionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(JsonResponse(
                """
                {
                  "value": [{
                    "id":42,
                    "fields":{
                      "System.Title":"Mentioned item",
                      "System.WorkItemType":"Bug",
                      "System.State":"Active",
                      "System.ChangedDate":"2026-09-10T00:00:00Z"
                    }
                  }]
                }
                """));
    }

    private sealed class SavedQueryHandler : HttpMessageHandler
    {
        private readonly bool _treeQuery;

        public SavedQueryHandler(bool treeQuery)
        {
            _treeQuery = treeQuery;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(_treeQuery
                    ? """
                      {
                        "queryType":"tree",
                        "workItemRelations":[
                          {"source":{"id":3},"target":{"id":1}},
                          {"source":{"id":3},"target":{"id":2}}
                        ]
                      }
                      """
                    : """{"queryType":"flat","workItems":[{"id":3},{"id":1},{"id":2}]}""");
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var ids = document.RootElement
                .GetProperty("ids")
                .EnumerateArray()
                .Select(item => item.GetInt32())
                .ToArray();
            Assert.Equal(
                _treeQuery ? new[] { 3, 1, 2 } : new[] { 3, 1 },
                ids);
            var values = ids.Reverse().Select(id => new
            {
                id,
                fields = new Dictionary<string, object?>
                {
                    ["System.Title"] = id.ToString()
                }
            });
            return JsonResponse(JsonSerializer.Serialize(new { value = values }));
        }
    }

    private sealed class PullRequestHandler : HttpMessageHandler
    {
        public string PullRequestQuery { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith(
                    "/_apis/connectionData",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(
                    """{"authenticatedUser":{"id":"user-1","displayName":"User One"}}"""));
            }

            PullRequestQuery = uri.Query;
            return Task.FromResult(JsonResponse(
                """
                {
                  "count": 2,
                  "value": [
                    {
                      "pullRequestId": 101,
                      "title": "Review me",
                      "status": "active",
                      "creationDate": "2026-09-01T00:00:00Z",
                      "sourceRefName": "refs/heads/feature",
                      "targetRefName": "refs/heads/main",
                      "repository": {"name":"repo one"},
                      "createdBy": {"displayName":"Author"},
                      "reviewers": [{"id":"user-1","vote":0,"isRequired":true}]
                    },
                    {
                      "pullRequestId": 102,
                      "title": "Already approved",
                      "status": "active",
                      "creationDate": "2026-09-02T00:00:00Z",
                      "repository": {"name":"repo two"},
                      "reviewers": [{"id":"user-1","vote":10}]
                    }
                  ]
                }
                """));
        }
    }

    private sealed class EmptyTreeQueryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(JsonResponse(
                """{"queryType":"tree","workItemRelations":[]}"""));
    }

    private sealed class RecentActivityHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith(
                    "/_apis/wit/wiql",
                    StringComparison.OrdinalIgnoreCase))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("@Today - 7", body, StringComparison.Ordinal);
                return JsonResponse("""{"workItems":[{"id":42}]}""");
            }

            if (uri.AbsolutePath.EndsWith(
                    "/_apis/wit/workitemsbatch",
                    StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """
                    {
                      "value": [{
                        "id":42,
                        "fields":{
                          "System.Title":"Recent item",
                          "System.WorkItemType":"Bug",
                          "System.State":"Active",
                          "Microsoft.VSTS.Common.Priority":1,
                          "System.AreaPath":"DevDiv",
                          "System.ChangedDate":"CURRENT_CHANGED_DATE"
                        }
                      }]
                    }
                    """.Replace(
                        "CURRENT_CHANGED_DATE",
                        DateTimeOffset.UtcNow.ToString("O"),
                        StringComparison.Ordinal));
            }

            return JsonResponse(
                """
                {
                  "comments": [
                    {"commentId":1,"text":"old","modifiedDate":"2026-09-01T00:00:00Z"},
                    {"commentId":3,"text":"new","modifiedDate":"2026-09-03T00:00:00Z"},
                    {"commentId":2,"text":"middle","modifiedDate":"2026-09-02T00:00:00Z"}
                  ]
                }
                """);
        }
    }

    private sealed class EmptyActivityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(JsonResponse("""{"workItems":[]}"""));
    }

    private sealed class PagedPullRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith(
                    "/_apis/connectionData",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(JsonResponse(
                    """{"authenticatedUser":{"id":"user-1","displayName":"User One"}}"""));
            }

            var skip = ParseQueryInt(uri.Query, "$skip");
            if (skip == 0)
            {
                var voted = Enumerable.Range(1, 100).Select(id => new
                {
                    pullRequestId = id,
                    title = $"Voted {id}",
                    status = "active",
                    creationDate = "2026-09-01T00:00:00Z",
                    repository = new { name = "repo" },
                    reviewers = new[] { new { id = "user-1", vote = 10 } }
                });
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(
                    new { count = 100, value = voted })));
            }

            return Task.FromResult(JsonResponse(
                """
                {
                  "count":1,
                  "value":[{
                    "pullRequestId":999,
                    "title":"Pending",
                    "status":"active",
                    "creationDate":"2026-09-02T00:00:00Z",
                    "repository":{"name":"repo"},
                    "reviewers":[{"id":"user-1","vote":0}]
                  }]
                }
                """));
        }
    }

    private sealed class CommentPagingHandler : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestCount++;
            var response = JsonResponse(_requestCount == 1
                ? """
                  {
                    "totalCount":5,
                    "comments":[
                      {"commentId":1,"text":"one"},
                      {"commentId":2,"text":"two"}
                    ]
                  }
                  """
                : """
                  {
                    "totalCount":5,
                    "comments":[{"commentId":3,"text":"three"}]
                  }
                  """);
            response.Headers.TryAddWithoutValidation(
                "x-ms-continuationtoken",
                _requestCount == 1 ? "next" : "still-more");
            return Task.FromResult(response);
        }
    }

    private sealed class FakeRecentMentionsProvider : IRecentMentionsProvider
    {
        private readonly IReadOnlyList<RecentMentionReference> _mentions;

        public FakeRecentMentionsProvider(
            params RecentMentionReference[] mentions)
        {
            _mentions = mentions;
        }

        public Task<IReadOnlyList<RecentMentionReference>>
            GetRecentMentionsAsync(
                string accessToken,
                CancellationToken cancellationToken)
            => Task.FromResult(_mentions);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static int ParseQueryInt(string query, string key)
    {
        foreach (var item in query.TrimStart('?').Split('&'))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 &&
                Uri.UnescapeDataString(pair[0]).Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(pair[1], out var value))
            {
                return value;
            }
        }

        return 0;
    }
}
