namespace WorkMonitor.Agent;

public enum AgentEventKind
{
    TurnStarted,
    TextDelta,
    ToolStarted,
    ApprovalRequested,
    ToolCompleted,
    Warning,
    Error,
    Completed
}

public sealed record AgentEvent(
    AgentEventKind Kind,
    string? Content = null,
    string? ToolName = null,
    string? ToolArguments = null,
    int? Turn = null);

public sealed record AgentConversationMessage(
    string Role,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record ConversationCompactionResult(
    string Summary,
    IReadOnlyList<AgentConversationMessage> RetainedMessages);
