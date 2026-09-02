using WorkMonitor.Agent;
using WorkMonitor.Cli;

namespace WorkMonitor.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public async Task PersistsResumesCompactsAndAuditsConversation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "workmonitor-tests",
            Guid.NewGuid().ToString("N"));
        var database = Path.Combine(directory, "sessions.db");

        try
        {
            var store = new SessionStore(database);
            await store.InitializeAsync(CancellationToken.None);
            var created = await store.CreateAsync(
                "fake-model",
                cancellationToken: CancellationToken.None);
            var user = new AgentConversationMessage(
                "user",
                "hello",
                DateTimeOffset.UtcNow);
            var assistant = new AgentConversationMessage(
                "assistant",
                "hi",
                DateTimeOffset.UtcNow);
            await store.AppendMessageAsync(
                created.Id,
                user,
                CancellationToken.None);
            await store.AppendMessageAsync(
                created.Id,
                assistant,
                CancellationToken.None);
            await store.AppendMessageAsync(
                created.Id,
                new AgentConversationMessage(
                    "tool",
                    "Tool 'write_test' completed with posted=true.",
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
            await store.AppendAuditEntriesAsync(
                created.Id,
                new[] { "approved write" },
                CancellationToken.None);

            var loaded = await store.LoadAsync(
                created.Id,
                CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal("tool", loaded.Messages[2].Role);
            Assert.Equal(new[]
                {
                    "hello",
                    "hi",
                    "Tool 'write_test' completed with posted=true."
                },
                loaded.Messages.Select(message => message.Content));

            await store.ReplaceHistoryAsync(
                created.Id,
                "Earlier greeting.",
                new[] { assistant },
                CancellationToken.None);

            var compacted = await store.LoadAsync(
                created.Id,
                CancellationToken.None);
            Assert.NotNull(compacted);
            Assert.Equal("Earlier greeting.", compacted.Summary);
            Assert.Single(compacted.Messages);

            var audit = await store.ListAuditEntriesAsync(
                created.Id,
                cancellationToken: CancellationToken.None);
            Assert.Single(audit);
            Assert.Equal("approved write", audit[0].Entry);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
