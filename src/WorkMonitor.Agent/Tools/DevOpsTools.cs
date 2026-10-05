using System.Text.Json;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Agent.Tools
{
    /// <summary>
    /// Helper for tools that need a MonitorClient bound to the caller's DevOps token.
    /// </summary>
    internal static class DevOpsToolHelpers
    {
        public static async ValueTask<MonitorClient> RequireClientAsync(
            AgentContext context,
            CancellationToken cancellationToken)
        {
            var token = await context.GetRequiredDevOpsTokenAsync(cancellationToken);
            return new MonitorClient(token);
        }
    }

    /// <summary>
    /// Searches DevOps work items inside the configured project area path using a
    /// substring match against title / description / comments.
    /// </summary>
    public class SearchWorkItemsTool : IAgentTool
    {
        public string Name => "search_work_items";
        public string Description =>
            "Search Azure DevOps work items in a DevDiv area for a given text. " +
            "Searches title, description, and comments. " +
            "Use this when the user asks to find tickets about a topic. " +
            "Returns the top matches (id, title, state, priority, assignedTo). " +
            "The 'projectArea' parameter is the friendly area key — call list_project_areas first if you don't know which to use.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""query"":        { ""type"": ""string"", ""description"": ""Text to search for."" },
                ""projectArea"":  { ""type"": ""string"", ""description"": ""Friendly area key from list_project_areas. Defaults to 'Project'."" },
                ""workItemType"": { ""type"": ""string"", ""enum"": [""Bug"",""Feedback"",""FeedbackTicket""], ""description"": ""Defaults to Bug. Feedback and FeedbackTicket both target FeedbackTicket items."" },
                ""maxResults"":   { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50, ""description"": ""Defaults to 10."" }
            },
            ""required"": [""query""]
        }").RootElement;

        public async Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
        {
            var query = args.GetProperty("query").GetString() ?? "";
            var projectArea = args.TryGetProperty("projectArea", out var p) ? (p.GetString() ?? "Project") : "Project";
            var type = args.TryGetProperty("workItemType", out var t) ? (t.GetString() ?? "Bug") : "Bug";
            var max = args.TryGetProperty("maxResults", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 10;
            type = AzureDevOpsQueryValues.NormalizeAgentSearchWorkItemType(type);

            var token = await ctx.GetRequiredDevOpsTokenAsync(ct);
            var service = new MonitorService(token);
            var matches = await service.SearchByMention(
                projectArea,
                query,
                type,
                ct);

            return new
            {
                count = matches.Count,
                items = matches.Take(max).Select(i => new
                {
                    id = i.id,
                    title = i.fields.Title,
                    state = i.fields.State,
                    priority = i.fields.Priority,
                    assignedTo = i.fields.AssignedTo?.displayName,
                    areaPath = i.fields.AreaPath
                })
            };
        }
    }

    /// <summary>
    /// Returns the configured project area keys so the LLM can pick a valid one.
    /// </summary>
    public class ListProjectAreasTool : IAgentTool
    {
        public string Name => "list_project_areas";
        public string Description =>
            "List the configured DevDiv project area keys (e.g. 'Project', 'Webtools'). " +
            "Other DevOps tools take one of these keys as 'projectArea'. " +
            "Call this first if you don't know which area key to use.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {}
        }").RootElement;

        public Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
        {
            var areas = Constants.ProjectAreaPath
                .Select(kv => new { key = kv.Key, areaPath = kv.Value })
                .ToList();
            return Task.FromResult<object>(new { areas });
        }
    }

    public sealed class ListMyWorkItemsTool : IAgentTool
    {
        public string Name => "list_my_work_items";

        public string Description =>
            "List the most recently changed active Azure DevOps work items assigned to " +
            "the currently authenticated user. Use this for requests containing 'my', " +
            "'assigned to me', or 'my DevDiv items'.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""workItemType"": {
                    ""type"": ""string"",
                    ""enum"": [""All"",""Bug"",""Feedback"",""FeedbackTicket"",""Task"",""Feature"",""User Story"",""Issue"",""Epic""],
                    ""description"": ""Optional type filter. Defaults to All.""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""minimum"": 1,
                    ""maximum"": 50,
                    ""description"": ""Maximum items to return. Defaults to 10.""
                }
            }
        }").RootElement;

        public async Task<object> ExecuteAsync(
            JsonElement args,
            AgentContext ctx,
            CancellationToken ct)
        {
            var type = args.TryGetProperty("workItemType", out var typeProperty)
                ? typeProperty.GetString()
                : null;
            var maxResults = args.TryGetProperty("maxResults", out var maxProperty) &&
                             maxProperty.ValueKind == JsonValueKind.Number
                ? maxProperty.GetInt32()
                : 10;

            var token = await ctx.GetRequiredDevOpsTokenAsync(ct);
            var client = new MonitorClient(token);
            var (items, totalCount) = await client.GetMyAssignedItems(
                maxResults,
                type,
                ct);

            return new
            {
                totalCount,
                returnedCount = items.Count,
                items = items.Select(item => new
                {
                    id = item.id,
                    title = item.fields.Title,
                    type = item.fields.WorkItemType,
                    state = item.fields.State,
                    priority = item.fields.Priority,
                    assignedTo = item.fields.AssignedTo?.displayName,
                    areaPath = item.fields.AreaPath,
                    changedDate = item.fields.ChangedDate
                })
            };
        }
    }

    public sealed class ListMyMentionedItemsTool : IAgentTool
    {
        public string Name => "list_items_mentioning_me";

        public string Description =>
            "List active Azure DevOps work items with recent identity-based " +
            "mentions of the currently authenticated user, newest mention first.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""workItemType"": {
                    ""type"": ""string"",
                    ""enum"": [""All"",""Bug"",""Feedback"",""FeedbackTicket"",""Task"",""Feature"",""User Story"",""Issue"",""Epic""],
                    ""description"": ""Optional type filter. Defaults to All.""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""minimum"": 1,
                    ""maximum"": 50,
                    ""description"": ""Maximum items to return. Defaults to 10.""
                }
            }
        }").RootElement;

        public async Task<object> ExecuteAsync(
            JsonElement args,
            AgentContext ctx,
            CancellationToken ct)
        {
            var type = args.TryGetProperty("workItemType", out var typeProperty)
                ? typeProperty.GetString()
                : null;
            var maxResults = args.TryGetProperty("maxResults", out var maxProperty) &&
                             maxProperty.ValueKind == JsonValueKind.Number
                ? maxProperty.GetInt32()
                : 10;

            var client = await DevOpsToolHelpers.RequireClientAsync(ctx, ct);
            var (items, totalCount) = await client.GetMyMentionedItems(
                maxResults,
                type,
                ct);

            return new
            {
                totalCount,
                returnedCount = items.Count,
                items = items.Select(ToWorkItemSummary)
            };
        }

        private static object ToWorkItemSummary(
            WorkMonitor.Core.Models.InternalItem item)
            => new
            {
                id = item.id,
                title = item.fields.Title,
                type = item.fields.WorkItemType,
                state = item.fields.State,
                priority = item.fields.Priority,
                assignedTo = item.fields.AssignedTo?.displayName,
                areaPath = item.fields.AreaPath,
                changedDate = item.fields.ChangedDate
            };
    }

    public sealed class ListSavedQueriesTool : IAgentTool
    {
        private readonly ParametersService _parameters;

        public ListSavedQueriesTool(ParametersService parameters)
        {
            _parameters = parameters;
        }

        public string Name => "list_saved_queries";

        public string Description =>
            "List the configured Azure DevOps saved-query names that can be run " +
            "with execute_saved_query.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(
            """{"type":"object","properties":{}}""").RootElement;

        public Task<object> ExecuteAsync(
            JsonElement args,
            AgentContext ctx,
            CancellationToken ct)
        {
            var queries = _parameters
                .GetSavedQueries()
                .OrderBy(query => query.Key, StringComparer.OrdinalIgnoreCase)
                .Select(query => new
                {
                    name = query.Key,
                    queryId = query.Value
                })
                .ToList();
            return Task.FromResult<object>(new
            {
                count = queries.Count,
                queries
            });
        }
    }

    public sealed class ExecuteSavedQueryTool : IAgentTool
    {
        private readonly ParametersService _parameters;

        public ExecuteSavedQueryTool(ParametersService parameters)
        {
            _parameters = parameters;
        }

        public string Name => "execute_saved_query";

        public string Description =>
            "Execute a configured Azure DevOps saved query by its friendly name " +
            "or GUID and return a bounded ordered list of work items.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""Friendly saved-query name or query GUID.""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""minimum"": 1,
                    ""maximum"": 50,
                    ""description"": ""Maximum items to return. Defaults to 20.""
                }
            },
            ""required"": [""query""]
        }").RootElement;

        public async Task<object> ExecuteAsync(
            JsonElement args,
            AgentContext ctx,
            CancellationToken ct)
        {
            var query = args.GetProperty("query").GetString();
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("Saved query name or id is required.");
            }

            var maxResults = args.TryGetProperty("maxResults", out var maxProperty) &&
                             maxProperty.ValueKind == JsonValueKind.Number
                ? maxProperty.GetInt32()
                : 20;
            var configured = _parameters.GetSavedQueries();
            var match = configured.FirstOrDefault(item =>
                item.Key.Equals(query, StringComparison.OrdinalIgnoreCase));
            var queryId = string.IsNullOrWhiteSpace(match.Value)
                ? query
                : match.Value;
            if (!Guid.TryParse(queryId, out _))
            {
                throw new KeyNotFoundException(
                    $"Saved query '{query}' was not found. Call list_saved_queries first.");
            }

            var client = await DevOpsToolHelpers.RequireClientAsync(ctx, ct);
            var (items, totalCount) = await client.GetWorkItemsFromSavedQuery(
                queryId,
                maxResults,
                ct);

            return new
            {
                query = string.IsNullOrWhiteSpace(match.Key) ? queryId : match.Key,
                queryId,
                totalCount,
                returnedCount = items.Count,
                items = items.Select(item => new
                {
                    id = item.id,
                    title = item.fields.Title,
                    type = item.fields.WorkItemType,
                    state = item.fields.State,
                    priority = item.fields.Priority,
                    assignedTo = item.fields.AssignedTo?.displayName,
                    areaPath = item.fields.AreaPath,
                    changedDate = item.fields.ChangedDate
                })
            };
        }
    }

    public sealed class ListPullRequestsForReviewTool : IAgentTool
    {
        public string Name => "list_pull_requests_for_my_review";

        public string Description =>
            "List active Azure DevOps pull requests requesting review from the " +
            "currently authenticated user, excluding pull requests already voted on.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""minimum"": 1,
                    ""maximum"": 50,
                    ""description"": ""Maximum pull requests to return. Defaults to 20.""
                }
            }
        }").RootElement;

        public async Task<object> ExecuteAsync(
            JsonElement args,
            AgentContext ctx,
            CancellationToken ct)
        {
            var maxResults = args.TryGetProperty("maxResults", out var maxProperty) &&
                             maxProperty.ValueKind == JsonValueKind.Number
                ? maxProperty.GetInt32()
                : 20;
            var client = await DevOpsToolHelpers.RequireClientAsync(ctx, ct);
            var pullRequests = await client.GetPullRequestsRequiringMyReview(
                maxResults,
                ct);

            return new
            {
                count = pullRequests.Count,
                pullRequests
            };
        }
    }

    public sealed class ListMyRecentActivityTool : IAgentTool
    {
        public string Name => "list_my_recent_activity";

        public string Description =>
            "List recently changed active Azure DevOps work assigned to or " +
            "mentioning the current user, including a bounded set of newest comments. " +
            "Partial-result flags identify candidate and output limits; use get_work_item " +
            "for further details.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""days"": {
                    ""type"": ""integer"",
                    ""minimum"": 1,
                    ""maximum"": 90,
                    ""description"": ""Lookback window in days. Defaults to 14.""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""minimum"": 1,
                    ""maximum"": 10,
                    ""description"": ""Maximum work items. Defaults to 10.""
                },
                ""commentsPerItem"": {
                    ""type"": ""integer"",
                    ""minimum"": 0,
                    ""maximum"": 2,
                    ""description"": ""Newest comments per item. Defaults to 1.""
                }
            }
        }").RootElement;

        public async Task<object> ExecuteAsync(
            JsonElement args,
            AgentContext ctx,
            CancellationToken ct)
        {
            var days = args.TryGetProperty("days", out var daysProperty) &&
                       daysProperty.ValueKind == JsonValueKind.Number
                ? daysProperty.GetInt32()
                : 14;
            var maxResults = args.TryGetProperty("maxResults", out var maxProperty) &&
                             maxProperty.ValueKind == JsonValueKind.Number
                ? maxProperty.GetInt32()
                : 10;
            var commentsPerItem =
                args.TryGetProperty("commentsPerItem", out var commentsProperty) &&
                commentsProperty.ValueKind == JsonValueKind.Number
                    ? commentsProperty.GetInt32()
                    : 1;

            var client = await DevOpsToolHelpers.RequireClientAsync(ctx, ct);
            return await client.GetMyRecentActivity(
                days,
                maxResults,
                commentsPerItem,
                ct);
        }
    }

    /// <summary>
    /// Reads a single DevOps work item plus its comments.
    /// </summary>
    public class GetWorkItemTool : IAgentTool
    {
        public string Name => "get_work_item";
        public string Description =>
            "Retrieve full details for a single Azure DevOps work item by id. " +
            "Returns title, description, state, priority, comments, and customer comments.";

        public bool IsWriteTool => false;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""id"": { ""type"": ""integer"", ""description"": ""Work item id."" }
            },
            ""required"": [""id""]
        }").RootElement;

        public async Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
        {
            var id = args.GetProperty("id").GetInt32();
            var token = await ctx.GetRequiredDevOpsTokenAsync(ct);
            var service = new MonitorService(token);
            return await service.GetWorkItemDetails(id, ct);
        }
    }

    /// <summary>
    /// Posts a comment on a DevOps work item. Honors AgentContext.DryRun.
    /// </summary>
    public class AddWorkItemCommentTool : IAgentTool
    {
        public string Name => "add_work_item_comment";
        public string Description =>
            "Post a comment on an Azure DevOps work item. " +
            "Use only when the user explicitly asks to add a comment / reply / note. " +
            "Respects dryRun: in dryRun mode the comment is NOT actually posted.";

        public bool IsWriteTool => true;

        public JsonElement ParametersSchema { get; } = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""id"":   { ""type"": ""integer"", ""description"": ""Work item id."" },
                ""text"": { ""type"": ""string"",  ""description"": ""HTML comment body to post."" }
            },
            ""required"": [""id"",""text""]
        }").RootElement;

        public async Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
        {
            var id = args.GetProperty("id").GetInt32();
            var text = args.GetProperty("text").GetString() ?? "";

            if (ctx.DryRun)
            {
                ctx.AuditLog.Add($"[dryRun] Would post comment on work item {id}: {text}");
                return new
                {
                    dryRun = true,
                    message = $"DRY RUN — comment NOT posted. Would have added the following text to work item {id}.",
                    text
                };
            }

            var client = await DevOpsToolHelpers.RequireClientAsync(ctx, ct);
            ct.ThrowIfCancellationRequested();
            await client.AddCommentToWorkItem(id, text, ct);
            ctx.AuditLog.Add($"Posted comment on work item {id} (by {ctx.UserName}).");
            return new { posted = true, id, textLength = text.Length };
        }
    }
}
