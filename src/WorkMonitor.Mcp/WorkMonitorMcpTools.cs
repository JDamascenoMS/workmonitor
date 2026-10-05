using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using WorkMonitor.Agent;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Mcp;

[McpServerToolType]
public sealed class WorkMonitorMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly AgentToolRegistry _tools;
    private readonly DevOpsTokenService _devOpsTokenService;
    private readonly bool _allowWrites;

    public WorkMonitorMcpTools(
        AgentToolRegistry tools,
        DevOpsTokenService devOpsTokenService,
        IConfiguration configuration)
    {
        _tools = tools;
        _devOpsTokenService = devOpsTokenService;
        _allowWrites = configuration.GetValue(
            "WorkMonitor:Mcp:AllowWrites",
            false);
    }

    [McpServerTool(
        Name = "search_work_items",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("Search Azure DevOps work items by text within a configured project area.")]
    public Task<object> SearchWorkItemsAsync(
        [Description("Text to search for.")] string query,
        [Description("Configured project area key.")] string projectArea = "Project",
        [Description("Work item type: Bug, Feedback, or FeedbackTicket.")] string workItemType = "Bug",
        [Description("Maximum number of matches, from 1 to 50.")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("A search query is required.", nameof(query));
        }

        if (maxResults is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 50.");
        }

        return ExecuteAsync(
            "search_work_items",
            new { query, projectArea, workItemType, maxResults },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_project_areas",
        ReadOnly = true)]
    [Description("List configured Azure DevOps project area keys and paths.")]
    public Task<object> ListProjectAreasAsync(
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "list_project_areas",
            new { },
            dryRun: true,
            cancellationToken);

    [McpServerTool(
        Name = "list_my_work_items",
        ReadOnly = true,
        OpenWorld = true)]
    [Description(
        "List recently changed active Azure DevOps work items assigned to the " +
        "currently authenticated user.")]
    public Task<object> ListMyWorkItemsAsync(
        [Description(
            "Optional type filter: All, Bug, Feedback, FeedbackTicket, Task, " +
            "Feature, User Story, Issue, or Epic.")]
        string workItemType = "All",
        [Description("Maximum number of items, from 1 to 50.")]
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (maxResults is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 50.");
        }

        return ExecuteAsync(
            "list_my_work_items",
            new { workItemType, maxResults },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_items_mentioning_me",
        ReadOnly = true,
        OpenWorld = true)]
    [Description(
        "List active Azure DevOps work items with recent identity-based " +
        "mentions of the currently authenticated user, newest mention first.")]
    public Task<object> ListItemsMentioningMeAsync(
        [Description(
            "Optional type filter: All, Bug, Feedback, FeedbackTicket, Task, " +
            "Feature, User Story, Issue, or Epic.")]
        string workItemType = "All",
        [Description("Maximum number of items, from 1 to 50.")]
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (maxResults is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 50.");
        }

        return ExecuteAsync(
            "list_items_mentioning_me",
            new { workItemType, maxResults },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_saved_queries",
        ReadOnly = true)]
    [Description("List configured Azure DevOps saved queries.")]
    public Task<object> ListSavedQueriesAsync(
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            "list_saved_queries",
            new { },
            dryRun: true,
            cancellationToken);

    [McpServerTool(
        Name = "execute_saved_query",
        ReadOnly = true,
        OpenWorld = true)]
    [Description(
        "Execute an Azure DevOps saved query by friendly name or query GUID.")]
    public Task<object> ExecuteSavedQueryAsync(
        [Description("Friendly saved-query name or query GUID.")]
        string query,
        [Description("Maximum number of items, from 1 to 50.")]
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException(
                "Saved query name or id is required.",
                nameof(query));
        }

        if (maxResults is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 50.");
        }

        return ExecuteAsync(
            "execute_saved_query",
            new { query, maxResults },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_pull_requests_for_my_review",
        ReadOnly = true,
        OpenWorld = true)]
    [Description(
        "List active Azure DevOps pull requests requesting review from the " +
        "currently authenticated user.")]
    public Task<object> ListPullRequestsForMyReviewAsync(
        [Description("Maximum number of pull requests, from 1 to 50.")]
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        if (maxResults is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 50.");
        }

        return ExecuteAsync(
            "list_pull_requests_for_my_review",
            new { maxResults },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_my_recent_activity",
        ReadOnly = true,
        OpenWorld = true)]
    [Description(
        "List recently changed active Azure DevOps work assigned to or " +
        "mentioning the current user, with newest comments.")]
    public Task<object> ListMyRecentActivityAsync(
        [Description("Lookback window in days, from 1 to 90.")]
        int days = 14,
        [Description("Maximum number of work items, from 1 to 10.")]
        int maxResults = 10,
        [Description("Newest comments per work item, from 0 to 2.")]
        int commentsPerItem = 1,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days),
                "days must be between 1 and 90.");
        }

        if (maxResults is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 10.");
        }

        if (commentsPerItem is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commentsPerItem),
                "commentsPerItem must be between 0 and 2.");
        }

        return ExecuteAsync(
            "list_my_recent_activity",
            new { days, maxResults, commentsPerItem },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "correlate_work_item_with_github",
        ReadOnly = true,
        OpenWorld = true)]
    [Description(
        "Find explicit GitHub issue or pull-request references related to one " +
        "Azure DevOps work item and return combined evidence.")]
    public Task<object> CorrelateWorkItemWithGitHubAsync(
        [Description("Azure DevOps work item id.")]
        int workItemId,
        [Description(
            "Optional configured GitHub area; all configured areas are searched " +
            "when omitted.")]
        string? area = null,
        [Description("Maximum correlations, from 1 to 5.")]
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        if (workItemId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workItemId),
                "workItemId must be positive.");
        }

        if (maxResults is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 5.");
        }

        return ExecuteAsync(
            "correlate_work_item_with_github",
            new { workItemId, area, maxResults },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "get_work_item",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("Retrieve an Azure DevOps work item and its comments by numeric id.")]
    public Task<object> GetWorkItemAsync(
        [Description("Azure DevOps work item id.")] int id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "id must be positive.");
        }

        return ExecuteAsync(
            "get_work_item",
            new { id },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "add_work_item_comment",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description(
        "Add a comment to an Azure DevOps work item. The call is a dry run unless " +
        "server writes are enabled and confirm is true.")]
    public Task<object> AddWorkItemCommentAsync(
        [Description("Azure DevOps work item id.")] int id,
        [Description("Comment text to add.")] string text,
        [Description("Explicitly confirm the write operation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), "id must be positive.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Comment text is required.", nameof(text));
        }

        return ExecuteAsync(
            "add_work_item_comment",
            new { id, text },
            dryRun: !_allowWrites || !confirm,
            cancellationToken);
    }

    [McpServerTool(
        Name = "list_github_issues",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("List issues from a configured GitHub repository area.")]
    public Task<object> ListGithubIssuesAsync(
        [Description("Configured GitHub repository area name.")] string area,
        [Description("Issue state: open, closed, or all.")] string state = "open",
        [Description("Optional comma-separated labels.")] string? labels = null,
        [Description("Maximum number of results, from 1 to 100.")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(area))
        {
            throw new ArgumentException("A GitHub area is required.", nameof(area));
        }

        if (maxResults is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResults),
                "maxResults must be between 1 and 100.");
        }

        return ExecuteAsync(
            "list_github_issues",
            new { area, state, labels, maxResults },
            dryRun: true,
            cancellationToken);
    }

    [McpServerTool(
        Name = "get_github_issue",
        ReadOnly = true,
        OpenWorld = true)]
    [Description("Retrieve one GitHub issue and its comments from a configured area.")]
    public Task<object> GetGithubIssueAsync(
        [Description("Configured GitHub repository area name.")] string area,
        [Description("GitHub issue number.")] int number,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(area))
        {
            throw new ArgumentException("A GitHub area is required.", nameof(area));
        }

        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(number),
                "number must be positive.");
        }

        return ExecuteAsync(
            "get_github_issue",
            new { area, number },
            dryRun: true,
            cancellationToken);
    }

    private async Task<object> ExecuteAsync(
        string toolName,
        object arguments,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!_tools.TryGet(toolName, out var tool))
        {
            throw new InvalidOperationException(
                $"The shared tool registry does not contain '{toolName}'.");
        }

        var context = new AgentContext
        {
            UserName = "mcp-client",
            DryRun = dryRun,
            DevOpsTokenProvider = token =>
                _devOpsTokenService.GetTokenAsync(token)
        };
        var element = JsonSerializer.SerializeToElement(arguments, JsonOptions);
        return await tool.ExecuteAsync(element, context, cancellationToken);
    }
}
