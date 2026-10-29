using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WorkMonitor.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkItemFieldUpdate
{
    [Description("New title, from 1 to 255 characters. Null or omitted leaves it unchanged.")]
    public string? Title { get; init; }

    [Description("New HTML description. Empty string clears it; null or omitted leaves it unchanged.")]
    public string? Description { get; init; }

    [Description("New state allowed by the work item's process. Null or omitted leaves it unchanged.")]
    public string? State { get; init; }

    [Description("Assignee email or identity. Empty string unassigns; null or omitted leaves it unchanged.")]
    public string? AssignedTo { get; init; }

    [Description("Nonnegative priority. Allowed values depend on the target work item type/process. Null or omitted leaves it unchanged.")]
    public int? Priority { get; init; }

    [Description("Complete replacement tag list. Empty array clears tags; null or omitted leaves them unchanged.")]
    public string[]? Tags { get; init; }

    public IReadOnlyDictionary<string, object> ToFieldValues()
    {
        var fields = new Dictionary<string, object>();
        if (Title != null)
        {
            if (string.IsNullOrWhiteSpace(Title) || Title.Length > 255)
                throw new ArgumentException("title must contain 1 to 255 characters.", nameof(Title));
            fields.Add("System.Title", Title);
        }

        if (Description != null)
            fields.Add("System.Description", Description);

        if (State != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(State);
            fields.Add("System.State", State);
        }

        if (AssignedTo != null)
        {
            if (AssignedTo.Length > 0 && string.IsNullOrWhiteSpace(AssignedTo))
                throw new ArgumentException("assignedTo must be an identity or an empty string to unassign.", nameof(AssignedTo));
            fields.Add("System.AssignedTo", AssignedTo);
        }

        if (Priority.HasValue)
        {
            if (Priority < 0)
                throw new ArgumentOutOfRangeException(nameof(Priority), "priority must be nonnegative; allowed values depend on the work item type/process.");
            fields.Add("Microsoft.VSTS.Common.Priority", Priority.Value);
        }

        if (Tags != null)
        {
            if (Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Contains(';')))
                throw new ArgumentException("Each tag must be nonempty and cannot contain a semicolon.", nameof(Tags));
            fields.Add("System.Tags", string.Join("; ", Tags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        if (fields.Count == 0)
            throw new ArgumentException("At least one field update is required.");

        return fields;
    }
}
