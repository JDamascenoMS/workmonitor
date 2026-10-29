using System.Text.Json;
using System.Text.Json.Serialization;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Models;

namespace WorkMonitor.Agent.Tools;

public sealed class CreateBugFromFeedbackTool : IAgentTool
{
    private static readonly JsonSerializerOptions ArgumentOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly Func<AgentContext, CancellationToken, ValueTask<MonitorClient>> _createClient;

    public CreateBugFromFeedbackTool() : this(DevOpsToolHelpers.RequireClientAsync) { }

    internal CreateBugFromFeedbackTool(
        Func<AgentContext, CancellationToken, ValueTask<MonitorClient>> createClient)
    {
        _createClient = createClient;
    }

    public string Name => "create_bug_from_feedback";
    public string Description =>
        "Only when explicitly requested, create an engineering Bug with a Related link to a source FeedbackTicket " +
        "in the SAME request. Requires an explicit engineering area, title, and HTML reproSteps; " +
        "never copy the feedback routing area blindly. Verifies source type, current Bug field metadata, " +
        "and Azure DevOps process rules before creation. Source/default state are left to process defaults " +
        "unless a source category is explicitly supplied. Does not separately change feedback state, assignment, or comments. " +
        "Dry runs never authenticate, create, or link anything. Creation is NOT idempotent: never retry automatically " +
        "after an uncertain outcome; search/verify the reported Bug and its link first.";
    public bool IsWriteTool => true;

    public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "feedbackTicketId": { "type": "integer", "minimum": 1 },
            "bug": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "title": { "type": "string", "minLength": 1, "maxLength": 255 },
                "areaPath": { "type": "string", "minLength": 1, "description": "Explicit engineering area path." },
                "reproSteps": { "type": "string", "minLength": 1, "description": "HTML Microsoft.VSTS.TCM.ReproSteps, not System.Description." },
                "description": { "type": ["string", "null"], "description": "Optional separate System.Description." },
                "iterationPath": { "type": ["string", "null"], "minLength": 1 },
                "assignedTo": { "type": ["string", "null"] },
                "priority": { "type": ["integer", "null"], "minimum": 0, "description": "Validated against current Bug process metadata." },
                "tags": { "type": ["array", "null"], "items": { "type": "string", "minLength": 1 } },
                "source": { "type": ["string", "null"], "minLength": 1, "description": "Optional explicit DevDiv Source category; omit to use process default." }
              },
              "required": ["title", "areaPath", "reproSteps"]
            }
          },
          "required": ["feedbackTicketId", "bug"]
        }
        """).RootElement.Clone();

    public async Task<object> ExecuteAsync(JsonElement args, AgentContext ctx, CancellationToken ct)
    {
        var input = args.Deserialize<CreateArguments>(ArgumentOptions)
            ?? throw new ArgumentException("Bug creation arguments are required.");
        if (input.FeedbackTicketId <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "feedbackTicketId must be positive.");
        ArgumentNullException.ThrowIfNull(input.Bug);
        var fields = input.Bug.ToFieldValues();
        ct.ThrowIfCancellationRequested();
        if (ctx.DryRun)
        {
            ctx.AuditLog.Add($"[dryRun] Would create Bug related to FeedbackTicket {input.FeedbackTicketId} (by {ctx.UserName}).");
            return new
            {
                dryRun = true,
                feedbackTicketId = input.FeedbackTicketId,
                workItemType = "Bug",
                fields,
                relation = new { rel = BugCreationFields.FeedbackRelation, url = MonitorClient.FeedbackWorkItemUrl(input.FeedbackTicketId) },
                message = "DRY RUN - no Bug or link created. Source type, field metadata, permissions, and required/conditional process rules have NOT been checked."
            };
        }

        if (ctx.UnconfirmedBugCreations.Contains(input.FeedbackTicketId))
        {
            ctx.AuditLog.Add($"Blocked automatic Bug creation retry for FeedbackTicket {input.FeedbackTicketId} (by {ctx.UserName}).");
            throw new InvalidOperationException(
                $"Blocked automatic Bug creation retry for FeedbackTicket {input.FeedbackTicketId} after an uncertain outcome. " +
                "Search for and verify the possible created Bug and its link before a new user-requested attempt.");
        }
        var client = await _createClient(ctx, ct);
        ct.ThrowIfCancellationRequested();
        ctx.AuditLog.Add($"Attempting Bug creation related to FeedbackTicket {input.FeedbackTicketId} (by {ctx.UserName}).");
        CreatedBugResult result;
        try
        {
            result = await client.CreateBugFromFeedback(input.FeedbackTicketId, input.Bug, ct);
        }
        catch (BugCreationOutcomeUnknownException ex)
        {
            ctx.UnconfirmedBugCreations.Add(input.FeedbackTicketId);
            ctx.AuditLog.Add($"UNCONFIRMED Bug creation (by {ctx.UserName}): {ex.Message}");
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or InvalidOperationException)
        {
            ctx.AuditLog.Add($"Bug creation failed (by {ctx.UserName}): {ex.Message}");
            throw;
        }
        ctx.AuditLog.Add($"Created Bug {result.Id} ({result.Url}) with verified Related link to FeedbackTicket {result.FeedbackTicketId} (by {ctx.UserName}).");
        return new
        {
            created = true,
            id = result.Id,
            url = result.Url,
            apiUrl = result.ApiUrl,
            feedbackTicketId = result.FeedbackTicketId,
            relation = new { rel = BugCreationFields.FeedbackRelation, url = result.RelationUrl, verified = true }
        };
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record CreateArguments
    {
        public required int FeedbackTicketId { get; init; }
        public required BugCreationFields Bug { get; init; }
    }
}
