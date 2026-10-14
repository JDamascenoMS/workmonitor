using System.Collections.Concurrent;

namespace FT_MONITOR.Services;

public sealed class ConversationTurnCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _active = new();

    public bool TryBegin(string conversationId)
        => _active.TryAdd(conversationId, 0);

    public void End(string conversationId)
        => _active.TryRemove(conversationId, out _);
}
