using WorkMonitor.Agent.Conversations;

namespace FT_MONITOR.Services;

public sealed class WebConversationStoreInitializer(SessionStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => store.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
