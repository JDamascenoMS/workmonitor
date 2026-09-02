using WorkMonitor.Agent;
using System.Text;

namespace WorkMonitor.Cli;

public sealed class ConsoleWriteApprovalHandler : IWriteApprovalHandler
{
    public async ValueTask<bool> ApproveAsync(
        WriteApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var previousColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine($"Write approval required: {request.ToolName}");
            Console.WriteLine(request.ArgumentsJson);
            Console.Write("Approve this operation? [y/N] ");
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }

        if (Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine(
                "Write denied: explicit approval requires an interactive console.");
            return false;
        }

        var response = await ReadInteractiveLineAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return
            string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(response?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadInteractiveLineAsync(
        CancellationToken cancellationToken)
    {
        var input = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                await Task.Delay(50, cancellationToken);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return input.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                {
                    input.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }
}
