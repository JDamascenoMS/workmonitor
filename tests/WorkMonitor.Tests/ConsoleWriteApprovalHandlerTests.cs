using WorkMonitor.Agent;
using WorkMonitor.Cli;

namespace WorkMonitor.Tests;

public sealed class ConsoleWriteApprovalHandlerTests
{
    [Fact]
    public async Task RedirectedInputCannotApproveWrites()
    {
        if (!Console.IsInputRedirected)
        {
            return;
        }

        var handler = new ConsoleWriteApprovalHandler();
        var approved = await handler.ApproveAsync(
            new WriteApprovalRequest(
                "write_test",
                "{}",
                "test",
                "session"),
            CancellationToken.None);

        Assert.False(approved);
    }
}
