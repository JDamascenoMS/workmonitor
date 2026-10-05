using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Helpers;

namespace WorkMonitor.Agent.Tools;

public sealed class CorrelateWorkItemWithGitHubTool : IAgentTool
{
    private static readonly Regex GitHubUrlRegex = new(
        @"https?://github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s]+)/(?<kind>issues|pull)/(?<number>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DevOpsUrlRegex = new(
        @"https?://(?:dev\.azure\.com|[a-z0-9-]+\.visualstudio\.com)/[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly GitHubClient _github;
    private readonly IConfiguration _configuration;
    private readonly Func<AgentContext, CancellationToken, ValueTask<MonitorClient>>
        _createDevOpsClient;

    public CorrelateWorkItemWithGitHubTool(
        GitHubClient github,
        IConfiguration configuration)
        : this(github, configuration, DevOpsToolHelpers.RequireClientAsync)
    {
    }

    internal CorrelateWorkItemWithGitHubTool(
        GitHubClient github,
        IConfiguration configuration,
        Func<AgentContext, CancellationToken, ValueTask<MonitorClient>> createDevOpsClient)
    {
        _github = github;
        _configuration = configuration;
        _createDevOpsClient = createDevOpsClient;
    }

    public string Name => "correlate_work_item_with_github";

    public string Description =>
        "Find explicit correlations between one Azure DevOps work item and " +
        "configured GitHub issues. Uses GitHub URLs in DevOps content plus exact " +
        "AB#<id> and work-item URL references in GitHub; it does not infer similarity.";

    public bool IsWriteTool => false;

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""workItemId"": {
                ""type"": ""integer"",
                ""minimum"": 1,
                ""description"": ""Azure DevOps work item id.""
            },
            ""area"": {
                ""type"": ""string"",
                ""description"": ""Optional configured GitHub area. Searches all configured areas when omitted.""
            },
            ""maxResults"": {
                ""type"": ""integer"",
                ""minimum"": 1,
                ""maximum"": 5,
                ""description"": ""Maximum correlated GitHub items. Defaults to 5.""
            }
        },
        ""required"": [""workItemId""]
    }").RootElement;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentContext ctx,
        CancellationToken ct)
    {
        var workItemId = args.GetProperty("workItemId").GetInt32();
        if (workItemId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workItemId),
                "workItemId must be positive.");
        }

        var area = args.TryGetProperty("area", out var areaProperty)
            ? areaProperty.GetString()
            : null;
        var maxResults = args.TryGetProperty("maxResults", out var maxProperty) &&
                         maxProperty.ValueKind == JsonValueKind.Number
            ? maxProperty.GetInt32()
            : 5;
        if (maxResults is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 5.");
        }

        var repositories = string.IsNullOrWhiteSpace(area)
            ? GitHubRepositoryCatalog.List(_configuration)
            : new[]
            {
                GitHubRepositoryCatalog.Resolve(_configuration, area)
                    ?? throw new InvalidOperationException(
                        $"Unknown GitHub area '{area}'.")
            };
        var devOps = await _createDevOpsClient(ctx, ct);
        var workItemResponse = await devOps.GetWorkItems(
            new List<int> { workItemId },
            ct);
        var workItem = workItemResponse.value?.SingleOrDefault()
            ?? throw new KeyNotFoundException(
                $"Azure DevOps work item {workItemId} was not found.");
        var commentsResponse = await devOps.GetCommentsForScanning(
            workItemId,
            maxComments: 500,
            ct);
        var devOpsTextParts = new[]
        {
            workItem.fields.Title,
            workItem.fields.Description,
            workItem.fields.UrlPostId
        }
        .Concat(commentsResponse.Comments.Select(comment => comment.text))
        .Where(value => !string.IsNullOrWhiteSpace(value));

        var correlations = new Dictionary<string, CorrelationCandidate>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var textPart in devOpsTextParts)
        {
            foreach (Match match in GitHubUrlRegex.Matches(textPart!))
            {
                var owner = match.Groups["owner"].Value;
                var repo = match.Groups["repo"].Value;
                var configured = repositories.FirstOrDefault(repository =>
                    repository.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                    repository.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase));
                if (configured == null ||
                    !int.TryParse(match.Groups["number"].Value, out var number))
                {
                    continue;
                }

                AddCandidate(
                    correlations,
                    configured,
                    number,
                    "Azure DevOps content contains an explicit GitHub URL.");
            }
        }

        foreach (var repository in repositories)
        {
            foreach (var searchTerm in new[]
            {
                $"\"AB#{workItemId}\" in:title,body",
                $"\"_workitems/edit/{workItemId}\" in:title,body"
            })
            {
                var search = await _github.SearchIssuesAsync(
                    repository.Owner,
                    repository.Repo,
                    searchTerm,
                    state: "all",
                    perPage: maxResults,
                    cancellationToken: ct,
                    itemType: "all");
                foreach (var issue in search?.Items ?? Enumerable.Empty<GitHubIssue>())
                {
                    AddCandidate(
                        correlations,
                        repository,
                        issue.Number,
                        evidence: null);
                }
            }
        }

        var correlatedItems = new List<object>();
        foreach (var candidate in correlations.Values)
        {
            if (correlatedItems.Count >= maxResults)
            {
                break;
            }

            GitHubIssue? issue;
            try
            {
                issue = await _github.GetIssueAsync(
                    candidate.Repository.Owner,
                    candidate.Repository.Repo,
                    candidate.Number,
                    ct);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == 404)
            {
                continue;
            }
            if (issue == null)
            {
                continue;
            }

            var issueText = $"{issue.Title}\n{issue.Body}";
            if (Regex.IsMatch(
                    issueText,
                    $@"(?<![A-Za-z0-9])AB#{workItemId}(?![A-Za-z0-9])",
                    RegexOptions.IgnoreCase))
            {
                candidate.Evidence.Add(
                    $"GitHub title/body explicitly references AB#{workItemId}.");
            }

            if (HasDevOpsReference(issueText, workItemId))
            {
                candidate.Evidence.Add(
                    $"GitHub title/body contains a DevDiv work-item URL for {workItemId}.");
            }

            if (candidate.Evidence.Count == 0)
            {
                continue;
            }

            const int commentsPerPage = 20;
            var lastPage = Math.Max(
                1,
                (int)Math.Ceiling(issue.Comments / (double)commentsPerPage));
            var comments = await _github.GetIssueCommentsAsync(
                candidate.Repository.Owner,
                candidate.Repository.Repo,
                candidate.Number,
                perPage: commentsPerPage,
                page: lastPage,
                cancellationToken: ct);
            correlatedItems.Add(new
            {
                area = candidate.Repository.Name,
                owner = candidate.Repository.Owner,
                repo = candidate.Repository.Repo,
                number = issue.Number,
                kind = issue.PullRequest == null ? "issue" : "pull_request",
                title = Truncate(issue.Title, 300),
                state = issue.State,
                htmlUrl = issue.HtmlUrl,
                updatedAt = issue.UpdatedAt,
                body = Truncate(issue.Body, 1000),
                recentComments = comments
                    .OrderByDescending(comment => comment.CreatedAt)
                    .Take(1)
                    .Select(comment => new
                    {
                        user = comment.User?.Login,
                        createdAt = comment.CreatedAt,
                        body = Truncate(comment.Body, 500)
                    }),
                evidence = candidate.Evidence.OrderBy(value => value).ToArray()
            });
        }

        var devOpsSummary = new
        {
                id = workItem.id,
                title = Truncate(workItem.fields.Title, 300),
                type = workItem.fields.WorkItemType,
                state = workItem.fields.State,
                assignedTo = workItem.fields.AssignedTo?.displayName,
                areaPath = workItem.fields.AreaPath,
                changedDate = workItem.fields.ChangedDate,
                recentComments = commentsResponse.Comments
                    .Where(comment => !comment.isDeleted)
                    .Take(3)
                    .Select(comment => new
                    {
                        modifiedDate = comment.modifiedDate,
                        text = Truncate(comment.text, 500)
                    }),
                commentsScanned = commentsResponse.Comments.Count,
                commentsTotal = commentsResponse.TotalCount,
                commentScanPartial = commentsResponse.IsPartial
        };

        return ReadResultBudget.FitCollection(
            correlatedItems,
            (items, truncated) => new
            {
                summary =
                    $"Returning {items.Count} of {correlatedItems.Count} retrieved " +
                    $"explicit GitHub correlations for Azure DevOps work item {workItemId}.",
                devOps = devOpsSummary,
                resultTruncated = truncated,
                correlations = items
            });
    }

    internal static IReadOnlyList<(string Owner, string Repo, string Kind, int Number)>
        ExtractGitHubReferences(string text)
    {
        return GitHubUrlRegex
            .Matches(text ?? string.Empty)
            .Cast<Match>()
            .Select(match => (
                match.Groups["owner"].Value,
                match.Groups["repo"].Value,
                match.Groups["kind"].Value,
                int.Parse(match.Groups["number"].Value)))
            .ToList();
    }

    internal static bool HasDevOpsReference(string? text, int workItemId)
    {
        foreach (Match match in DevOpsUrlRegex.Matches(text ?? string.Empty))
        {
            var value = match.Value.TrimEnd('.', ',', ')', ']', ';');
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var segments = uri.AbsolutePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            var organizationMatches = uri.Host.Equals(
                "dev.azure.com",
                StringComparison.OrdinalIgnoreCase)
                ? segments.FirstOrDefault()?.Equals(
                    Constants.Organization,
                    StringComparison.OrdinalIgnoreCase) == true
                : uri.Host.Equals(
                    $"{Constants.Organization}.visualstudio.com",
                    StringComparison.OrdinalIgnoreCase);
            if (!organizationMatches)
            {
                continue;
            }

            for (var index = 0; index + 2 < segments.Length; index++)
            {
                if (segments[index].Equals("_workitems", StringComparison.OrdinalIgnoreCase) &&
                    segments[index + 1].Equals("edit", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(segments[index + 2], out var referencedId) &&
                    referencedId == workItemId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddCandidate(
        IDictionary<string, CorrelationCandidate> candidates,
        ConfiguredGitHubRepository repository,
        int number,
        string? evidence)
    {
        var key = $"{repository.Owner}/{repository.Repo}#{number}";
        if (!candidates.TryGetValue(key, out var candidate))
        {
            candidate = new CorrelationCandidate(repository, number);
            candidates.Add(key, candidate);
        }

        if (evidence != null)
        {
            candidate.Evidence.Add(evidence);
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "\n[truncated]";
    }

    private sealed class CorrelationCandidate
    {
        public CorrelationCandidate(
            ConfiguredGitHubRepository repository,
            int number)
        {
            Repository = repository;
            Number = number;
        }

        public ConfiguredGitHubRepository Repository { get; }
        public int Number { get; }
        public HashSet<string> Evidence { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
