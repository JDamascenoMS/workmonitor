using System.Text.Json;
using WorkMonitor.Agent;

namespace WorkMonitor.Tests;

internal sealed class FakeAgentModel : IAgentModel
{
    private readonly Queue<IReadOnlyList<AgentModelUpdate>> _responses = new();

    public string ModelName => "fake-model";

    public List<IReadOnlyList<AgentModelMessage>> Requests { get; } = new();

    public void Enqueue(params AgentModelUpdate[] updates)
        => _responses.Enqueue(updates);

    public async IAsyncEnumerable<AgentModelUpdate> StreamAsync(
        IReadOnlyList<AgentModelMessage> messages,
        IReadOnlyList<AgentToolDefinition> tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No fake model response was queued.");
        }

        foreach (var update in _responses.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }
}

internal sealed class FakeTool : IAgentTool
{
    private readonly Func<object> _resultFactory;

    public FakeTool(
        string name = "lookup",
        bool isWriteTool = false,
        Func<object>? resultFactory = null)
    {
        Name = name;
        IsWriteTool = isWriteTool;
        _resultFactory = resultFactory ?? (() => new { value = 42 });
    }

    public string Name { get; }
    public string Description => "A deterministic test tool.";
    public JsonElement ParametersSchema { get; } =
        JsonDocument.Parse("""{"type":"object","properties":{}}""")
            .RootElement
            .Clone();
    public bool IsWriteTool { get; }
    public int ExecutionCount { get; private set; }

    public Task<object> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken)
    {
        ExecutionCount++;
        return Task.FromResult(_resultFactory());
    }
}
