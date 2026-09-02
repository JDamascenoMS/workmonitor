using WorkMonitor.Agent;

namespace WorkMonitor.Tests;

public sealed class ConversationCompactorTests
{
    [Fact]
    public async Task CompactsOlderMessagesAndRetainsRecentMessages()
    {
        var model = new FakeAgentModel();
        model.Enqueue(
            new AgentModelUpdate(TextDelta: "Summary"),
            new AgentModelUpdate(
                CompletedResponse: new AgentModelResponse(
                    "Summary",
                    Array.Empty<AgentToolCall>())));
        var compactor = new ConversationCompactor(model);
        var messages = Enumerable.Range(1, 6)
            .Select(index => new AgentConversationMessage(
                index % 2 == 0 ? "assistant" : "user",
                $"message {index}",
                DateTimeOffset.UtcNow.AddMinutes(index)))
            .ToList();

        var result = await compactor.CompactAsync(
            null,
            messages,
            retainRecentMessages: 2,
            CancellationToken.None);

        Assert.Equal("Summary", result.Summary);
        Assert.Equal(new[] { "message 5", "message 6" },
            result.RetainedMessages.Select(message => message.Content));
        Assert.Contains("message 1", model.Requests[0][1].Content);
        Assert.DoesNotContain("message 6", model.Requests[0][1].Content);
    }

    [Fact]
    public void PendingPromptParticipatesInContextBudget()
    {
        var model = new FakeAgentModel();
        var compactor = new ConversationCompactor(model);
        var messages = new[]
        {
            new AgentConversationMessage(
                "user",
                new string('x', 40),
                DateTimeOffset.UtcNow)
        };

        Assert.False(compactor.NeedsCompaction(null, messages, 100));
        Assert.True(compactor.NeedsCompaction(
            null,
            messages,
            100,
            additionalCharacters: 61));
    }
}
