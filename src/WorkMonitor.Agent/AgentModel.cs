namespace WorkMonitor.Agent;

public enum AgentModelMessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record AgentToolCall(string Id, string Name, string ArgumentsJson);

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    string ParametersJson);

public sealed record AgentModelMessage(
    AgentModelMessageRole Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<AgentToolCall>? ToolCalls = null);

public sealed record AgentModelResponse(
    string Content,
    IReadOnlyList<AgentToolCall> ToolCalls);

public sealed record AgentModelUpdate(
    string? TextDelta = null,
    AgentModelResponse? CompletedResponse = null);

public interface IAgentModel
{
    string ModelName { get; }

    IAsyncEnumerable<AgentModelUpdate> StreamAsync(
        IReadOnlyList<AgentModelMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken = default);
}

public static class AgentModelExtensions
{
    public static async Task<string> CompleteTextAsync(
        this IAgentModel model,
        IReadOnlyList<AgentModelMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var content = new System.Text.StringBuilder();

        await foreach (var update in model.StreamAsync(
            messages,
            Array.Empty<AgentToolDefinition>(),
            cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.TextDelta))
            {
                content.Append(update.TextDelta);
            }

            if (update.CompletedResponse is { Content.Length: > 0 } completed &&
                content.Length == 0)
            {
                content.Append(completed.Content);
            }
        }

        return content.ToString();
    }
}
