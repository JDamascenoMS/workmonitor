using System.Text.Json;
using FT_MONITOR.Clients;
using FT_MONITOR.Helpers;
using FT_MONITOR.Services.Agent;

namespace FT_MONITOR.Services.Agent.Tools
{
    /// <summary>
    /// Helper for tools that need a MonitorClient bound to the caller's DevOps token.
    /// </summary>
    internal static class DevOpsToolHelpers
    {
        public static MonitorClient RequireClient(AgentContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.DevOpsToken))
                throw new InvalidOperationException("Azure DevOps token not available for this request.");
            return new MonitorClient(ctx.DevOpsToken);
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
                ""workItemType"": { ""type"": ""string"", ""enum"": [""Bug"",""Feedback""], ""description"": ""Defaults to Bug."" },
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

            var token = ctx.DevOpsToken ?? throw new InvalidOperationException("DevOps token unavailable.");
            var service = new MonitorService(token);
            var matches = await service.SearchByMention(projectArea, query, type);

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
            var token = ctx.DevOpsToken ?? throw new InvalidOperationException("DevOps token unavailable.");
            var service = new MonitorService(token);
            return await service.GetWorkItemDetails(id);
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

            var client = DevOpsToolHelpers.RequireClient(ctx);
            await client.AddCommentToWorkItem(id, text);
            ctx.AuditLog.Add($"Posted comment on work item {id} (by {ctx.UserName}).");
            return new { posted = true, id, text };
        }
    }
}
