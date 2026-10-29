using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WorkMonitor.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BugCreationFields
{
    public const string FeedbackRelation = "System.LinkTypes.Related";

    [Description("Bug title, 1 to 255 characters.")]
    public required string Title { get; init; }

    [Description("Explicit engineering area path, not the FeedbackTicket's routing area.")]
    public required string AreaPath { get; init; }

    [Description("HTML reproduction steps, expected behavior, and actual behavior. Maps to Microsoft.VSTS.TCM.ReproSteps.")]
    public required string ReproSteps { get; init; }

    [Description("Optional separate System.Description HTML; does not replace Repro Steps.")]
    public string? Description { get; init; }

    public string? IterationPath { get; init; }
    public string? AssignedTo { get; init; }

    [Description("Optional priority validated against current Bug process metadata; DevDiv currently allows 0, 1, 2, 3.")]
    public int? Priority { get; init; }

    public string[]? Tags { get; init; }

    [Description("Optional Microsoft.DevDiv.Source category, validated against process metadata. Omit to use the process default; do not infer a customer category.")]
    public string? Source { get; init; }

    public IReadOnlyDictionary<string, object> ToFieldValues()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(AreaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ReproSteps);
        var fields = new Dictionary<string, object>(new WorkItemFieldUpdate
        {
            Title = Title,
            Description = Description,
            AssignedTo = AssignedTo,
            Priority = Priority,
            Tags = Tags
        }.ToFieldValues())
        {
            ["System.AreaPath"] = AreaPath,
            ["Microsoft.VSTS.TCM.ReproSteps"] = ReproSteps
        };
        if (IterationPath != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(IterationPath);
            fields.Add("System.IterationPath", IterationPath);
        }
        if (Source != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Source);
            fields.Add("Microsoft.DevDiv.Source", Source);
        }
        return fields;
    }
}

public sealed record CreatedBugResult(
    int Id,
    string Url,
    string ApiUrl,
    int FeedbackTicketId,
    string RelationUrl);

public sealed class BugCreationOutcomeUnknownException : Exception
{
    public int? ReportedBugId { get; }
    public string? ReportedBugUrl { get; }

    public BugCreationOutcomeUnknownException(
        int feedbackTicketId,
        string detail,
        int? reportedBugId = null,
        string? reportedBugUrl = null,
        Exception? innerException = null)
        : base(
            "Bug creation outcome is uncertain. DO NOT retry creation automatically. " +
            (reportedBugId.HasValue
                ? $"Reported Bug {reportedBugId} ({reportedBugUrl}); verify it"
                : "Search for a possible created Bug") +
            $" and its Related link to FeedbackTicket {feedbackTicketId} before any new attempt. {detail}",
            innerException)
    {
        ReportedBugId = reportedBugId;
        ReportedBugUrl = reportedBugUrl;
    }
}
