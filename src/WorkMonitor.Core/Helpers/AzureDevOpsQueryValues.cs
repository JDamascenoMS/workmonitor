namespace WorkMonitor.Core.Helpers;

public static class AzureDevOpsQueryValues
{
    public static string NormalizeWorkItemType(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "BUG" => "Bug",
            "TASK" => "Task",
            "USERSTORY" => "User Story",
            "USER STORY" => "User Story",
            "EPIC" => "Epic",
            "ISSUE" => "Issue",
            "FEATURE" => "Feature",
            "FEEDBACK" => "FeedbackTicket",
            "FEEDBACKTICKET" => "FeedbackTicket",
            _ => throw new ArgumentException(
                "Unsupported Azure DevOps work item type.",
                nameof(value))
        };
    }

    public static string NormalizeAgentSearchWorkItemType(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "BUG" => "Bug",
            "FEEDBACK" => "FeedbackTicket",
            "FEEDBACKTICKET" => "FeedbackTicket",
            _ => throw new ArgumentException(
                "Agent search work item type must be Bug, Feedback, or FeedbackTicket.",
                nameof(value))
        };
    }

    public static string EscapeLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
