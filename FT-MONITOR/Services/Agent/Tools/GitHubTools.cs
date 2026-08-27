using System.Text.Json;
using FT_MONITOR.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace FT_MONITOR.Services.Agent.Tools
{
    /// <summary>
    /// Lists issues from a configured GitHub repository (area).
    /// </summary>
    public class ListGithubIssuesTool : IAgentTool
    {
        private readonly IServiceProvider _sp;
        private readonly IConfiguration _config;

        public ListGithubIssuesTool(IServiceProvider sp, IConfiguration config)
        {
            _sp = sp;
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

            // GitHubClient is a typed HttpClient (transient) so resolve per-call from a scope.
            using var scope = _sp.CreateScope();
            var gh = scope.ServiceProvider.GetRequiredService<GitHubClient>();
            var (issues, _, _, _) = await gh.ListIssuesAsync(owner, repo, state, labels, perPage: Math.Min(max, 100));
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
        private readonly IServiceProvider _sp;
        private readonly IConfiguration _config;

        public GetGithubIssueTool(IServiceProvider sp, IConfiguration config)
        {
            _sp = sp;
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

            using var scope = _sp.CreateScope();
            var gh = scope.ServiceProvider.GetRequiredService<GitHubClient>();
            var issue = await gh.GetIssueAsync(resolved.Owner, resolved.Repo, number);
            if (issue == null) return new { found = false };
            var comments = await gh.GetIssueCommentsAsync(resolved.Owner, resolved.Repo, number);

            return new
            {
                found = true,
                issue = new
                {
                    number = issue.Number,
                    title = issue.Title,
                    state = issue.State,
                    body = issue.Body,
                    htmlUrl = issue.HtmlUrl,
                    labels = issue.Labels?.Select(l => l.Name),
                    assignees = issue.Assignees?.Select(a => a.Login),
                    user = issue.User?.Login
                },
                comments = comments.Select(c => new
                {
                    user = c.User?.Login,
                    createdAt = c.CreatedAt,
                    body = c.Body
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
}

