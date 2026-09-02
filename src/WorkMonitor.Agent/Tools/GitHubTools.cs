using System.Text.Json;
using WorkMonitor.Core.Clients;
using Microsoft.Extensions.Configuration;

namespace WorkMonitor.Agent.Tools
{
    /// <summary>
    /// Lists issues from a configured GitHub repository (area).
    /// </summary>
    public class ListGithubIssuesTool : IAgentTool
    {
        private readonly GitHubClient _github;
        private readonly IConfiguration _config;

        public ListGithubIssuesTool(GitHubClient github, IConfiguration config)
        {
            _github = github;
            _config = config;
        }

        public string Name => "list_github_issues";
        public string Description =>
            "List GitHub issues from a configured repository (area). " +
            "Use when the user mentions GitHub issues. " +
            "Returns id, number, title, state, labels, comments count, updatedAt.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""area"":     { ""type"": ""string"", ""description"": ""Configured area name (e.g. 'vscode-dotnettools')."" },
                ""state"":    { ""type"": ""string"", ""enum"": [""open"",""closed"",""all""], ""description"": ""Defaults to open."" },
                ""labels"":   { ""type"": ""string"", ""description"": ""Comma-separated label names."" },
                ""maxResults"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100, ""description"": ""Defaults to 20."" }
            },
            ""required"": [""area""]
        }").RootElement;

        public async Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
        {
            var area = args.GetProperty("area").GetString() ?? "";
            var state = args.TryGetProperty("state", out var s) ? (s.GetString() ?? "open") : "open";
            var labels = args.TryGetProperty("labels", out var l) ? l.GetString() : null;
            var max = args.TryGetProperty("maxResults", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 20;

            var (owner, repo) = ResolveArea(area)
                ?? throw new InvalidOperationException($"Unknown GitHub area '{area}'. Configured areas come from GitHub:Repositories in appsettings.");

            var (issues, _, _, _) = await _github.ListIssuesAsync(
                owner,
                repo,
                state,
                labels,
                perPage: Math.Min(max, 100),
                cancellationToken: ct);
            return new
            {
                area,
                count = issues.Count,
                items = issues.Take(max).Select(i => new
                {
                    number = i.Number,
                    title = i.Title,
                    state = i.State,
                    labels = i.Labels?.Select(x => x.Name),
                    comments = i.Comments,
                    htmlUrl = i.HtmlUrl,
                    updatedAt = i.UpdatedAt,
                    user = i.User?.Login,
                    assignees = i.Assignees?.Select(a => a.Login)
                })
            };
        }

        private (string Owner, string Repo)? ResolveArea(string area)
        {
            foreach (var child in _config.GetSection("GitHub:Repositories").GetChildren())
            {
                var name = child["name"];
                var owner = child["owner"];
                var repo = child["repo"];
                if (string.Equals(name, area, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
                {
                    return (owner!, repo!);
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Reads a single GitHub issue + comments.
    /// </summary>
    public class GetGithubIssueTool : IAgentTool
    {
        private readonly GitHubClient _github;
        private readonly IConfiguration _config;

        public GetGithubIssueTool(GitHubClient github, IConfiguration config)
        {
            _github = github;
            _config = config;
        }

        public string Name => "get_github_issue";
        public string Description =>
            "Retrieve a single GitHub issue (title, body, labels, assignees, state) along with its comment thread.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""area"":   { ""type"": ""string"", ""description"": ""Configured area name."" },
                ""number"": { ""type"": ""integer"", ""description"": ""Issue number (#42)."" }
            },
            ""required"": [""area"",""number""]
        }").RootElement;

        public async Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
        {
            var area = args.GetProperty("area").GetString() ?? "";
            var number = args.GetProperty("number").GetInt32();

            var resolved = ResolveArea(area)
                ?? throw new InvalidOperationException($"Unknown GitHub area '{area}'.");

            var issue = await _github.GetIssueAsync(
                resolved.Owner,
                resolved.Repo,
                number,
                ct);
            if (issue == null) return new { found = false };
            const int commentLimit = 30;
            var lastCommentPage = Math.Max(
                1,
                (int)Math.Ceiling(issue.Comments / (double)commentLimit));
            var comments = await _github.GetIssueCommentsAsync(
                resolved.Owner,
                resolved.Repo,
                number,
                perPage: commentLimit,
                page: lastCommentPage,
                cancellationToken: ct);
            if (comments.Count < 5 && lastCommentPage > 1)
            {
                var precedingComments = await _github.GetIssueCommentsAsync(
                    resolved.Owner,
                    resolved.Repo,
                    number,
                    perPage: commentLimit,
                    page: lastCommentPage - 1,
                    cancellationToken: ct);
                comments = precedingComments.Concat(comments).ToList();
            }
            var selectedComments = comments
                .OrderByDescending(comment => comment.CreatedAt)
                .Take(5)
                .ToList();
            var omittedComments = Math.Max(
                0,
                issue.Comments - selectedComments.Count);

            return new
            {
                found = true,
                issue = new
                {
                    number = issue.Number,
                    title = issue.Title,
                    state = issue.State,
                    body = Truncate(issue.Body, 6000),
                    htmlUrl = issue.HtmlUrl,
                    labels = issue.Labels?.Select(l => l.Name),
                    assignees = issue.Assignees?.Select(a => a.Login),
                    user = issue.User?.Login
                },
                commentsTruncated = omittedComments > 0,
                omittedComments,
                comments = selectedComments
                    .Select(c => new
                    {
                        user = c.User?.Login,
                        createdAt = c.CreatedAt,
                        body = Truncate(c.Body, 2000)
                    })
            };
        }

        private (string Owner, string Repo)? ResolveArea(string area)
        {
            foreach (var child in _config.GetSection("GitHub:Repositories").GetChildren())
            {
                var name = child["name"];
                var owner = child["owner"];
                var repo = child["repo"];
                if (string.Equals(name, area, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
                {
                    return (owner!, repo!);
                }
            }
            return null;
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength] + "\n[truncated]";
        }
    }
}
