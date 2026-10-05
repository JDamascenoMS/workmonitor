using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;

namespace WorkMonitor.Tests;

public sealed class GitHubToolsTests
{
    [Fact]
    public async Task RetrievesFiveNewestCommentsAcrossPageBoundary()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Repositories:0:name"] = "test",
                ["GitHub:Repositories:0:owner"] = "owner",
                ["GitHub:Repositories:0:repo"] = "repo"
            })
            .Build();
        var client = new GitHubClient(
            new HttpClient(new GitHubHandler()),
            configuration);
        var tool = new GetGithubIssueTool(client, configuration);
        using var arguments = JsonDocument.Parse(
            """{"area":"test","number":1}""");

        var result = await tool.ExecuteAsync(
            arguments.RootElement,
            new AgentContext(),
            CancellationToken.None);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"omittedComments\":26", json);
        Assert.Contains("comment-31", json);
        Assert.Contains("comment-27", json);
        Assert.DoesNotContain("comment-26", json);
        Assert.True(
            json.IndexOf("comment-31", StringComparison.Ordinal) <
            json.IndexOf("comment-30", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAllIncludesIssuesAndPullRequests()
    {
        var configuration = new ConfigurationBuilder().Build();
        var handler = new SearchCaptureHandler();
        var client = new GitHubClient(new HttpClient(handler), configuration);

        await client.SearchIssuesAsync(
            "owner",
            "repo",
            "\"AB#42\"",
            itemType: "all",
            cancellationToken: CancellationToken.None);

        Assert.DoesNotContain("is%3Aissue", handler.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("is%3Apr", handler.Query, StringComparison.Ordinal);
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Request URI is required.");

            if (!uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "id": 1,
                      "number": 1,
                      "title": "Issue",
                      "state": "open",
                      "body": "body",
                      "comments": 31
                    }
                    """));
            }

            var page = ParsePage(uri.Query);
            var first = page == 2 ? 31 : 1;
            var last = page == 2 ? 31 : 30;
            var comments = Enumerable.Range(first, last - first + 1)
                .Select(index => new
                {
                    id = index,
                    body = $"comment-{index}",
                    created_at = new DateTime(2026, 1, 1).AddDays(index)
                });
            return Task.FromResult(
                JsonResponse(JsonSerializer.Serialize(comments)));
        }

        private static int ParsePage(string query)
        {
            foreach (var item in query.TrimStart('?').Split('&'))
            {
                var pair = item.Split('=', 2);
                if (pair.Length == 2 &&
                    pair[0].Equals("page", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(pair[1], out var page))
                {
                    return page;
                }
            }

            return 1;
        }
    }

    private sealed class SearchCaptureHandler : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Query = request.RequestUri?.Query ?? string.Empty;
            return Task.FromResult(JsonResponse(
                """{"total_count":0,"items":[]}"""));
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
