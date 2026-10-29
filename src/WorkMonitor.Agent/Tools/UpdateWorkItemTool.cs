using System.Text.Json;
using System.Text.Json.Serialization;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Models;

namespace WorkMonitor.Agent.Tools;

public sealed class UpdateWorkItemTool : IAgentTool
{
    private static readonly JsonSerializerOptions ArgumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<AgentContext, CancellationToken, ValueTask<MonitorClient>> _createClient;

    public UpdateWorkItemTool() : this(DevOpsToolHelpers.RequireClientAsync) { }

    internal UpdateWorkItemTool(
        Func<AgentContext, CancellationToken, ValueTask<MonitorClient>> createClient)
    {
        _createClient = createClient;
    }

    public string Name => "update_work_item";
    public string Description =>
        "Update only the explicitly requested title, description, state, assignment, priority, or tags " +
        "of an existing Azure DevOps work item. Read get_work_item first and use its revision as expectedRevision. " +
        "Omitted/null fields stay unchanged. Empty description clears it, empty assignedTo unassigns, " +
        "and an empty tags array clears tags; tags otherwise replace the entire list. " +
        "Use only when the user explicitly requests these edits. Dry runs never write. " +
        "On a revision conflict, re-read and obtain fresh approval; never retry blindly.";
    public bool IsWriteTool => true;

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "id": { "type": "integer", "minimum": 1, "description": "Work item id." },
            "expectedRevision": { "type": "integer", "minimum": 1, "description": "Revision returned by get_work_item." },
            "fields": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "title": { "type": ["string", "null"], "minLength": 1, "maxLength": 255 },
                "description": { "type": ["string", "null"], "description": "HTML description; empty string clears." },
                "state": { "type": ["string", "null"], "minLength": 1 },
                "assignedTo": { "type": ["string", "null"], "description": "Assignee identity/email; empty string unassigns." },
                "priority": { "type": ["integer", "null"], "minimum": 0, "description": "Allowed values depend on the target work item type/process; validated by Azure DevOps on live writes." },
                "tags": { "type": ["array", "null"], "items": { "type": "string", "minLength": 1 }, "description": "Replaces all tags; empty array clears. No semicolons in individual tags." }
              }
            }
          },
          "required": ["id", "expectedRevision", "fields"]
        }
        """).RootElement.Clone();

    public async Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
    {
        var request = args.Deserialize<UpdateArguments>(ArgumentOptions)
            ?? throw new ArgumentException("Update arguments are required.");
        if (request.Id <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "id must be positive.");
        if (request.ExpectedRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "expectedRevision must be positive. Read get_work_item first.");
        ArgumentNullException.ThrowIfNull(request.Fields);
        var fields = request.Fields.ToFieldValues();
        var fieldNames = string.Join(", ", fields.Keys);
        ct.ThrowIfCancellationRequested();

        if (ctx.DryRun)
        {
            ctx.AuditLog.Add($"[dryRun] Would update work item {request.Id} at revision {request.ExpectedRevision}: {fieldNames} (by {ctx.UserName}).");
            return new
            {
                dryRun = true,
                id = request.Id,
                expectedRevision = request.ExpectedRevision,
                fields,
                message = "DRY RUN - work item NOT updated. Revision and process rules will be checked on a live write."
            };
        }

        var client = await _createClient(ctx, ct);
        ct.ThrowIfCancellationRequested();
        ctx.AuditLog.Add($"Attempting update of work item {request.Id} at revision {request.ExpectedRevision}: {fieldNames} (by {ctx.UserName}).");
        var item = await client.UpdateWorkItem(request.Id, request.ExpectedRevision, request.Fields, ct);
        ctx.AuditLog.Add($"Updated work item {request.Id} to revision {item.rev}: {fieldNames} (by {ctx.UserName}).");
        return new { updated = true, id = item.id, revision = item.rev, updatedFields = fields.Keys };
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record UpdateArguments
    {
        public required int Id { get; init; }
        public required int ExpectedRevision { get; init; }
        public required WorkItemFieldUpdate Fields { get; init; }
    }
}
