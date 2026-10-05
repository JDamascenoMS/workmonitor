using ModelContextProtocol.Client;

namespace WorkMonitor.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task StdioServerInitializesAndListsSharedTools()
    {
        var serverPath = Path.Combine(
            AppContext.BaseDirectory,
            "workmonitor-mcp.dll");
        Assert.True(
            File.Exists(serverPath),
            $"MCP server assembly was not found at '{serverPath}'.");

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "WorkMonitor test server",
                Command = FindDotnetHost(),
                Arguments = new[] { serverPath },
                WorkingDirectory = AppContext.BaseDirectory
            });
        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync(
            cancellationToken: CancellationToken.None);
        var names = tools.Select(tool => tool.Name).ToHashSet(
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(13, names.Count);
        Assert.Contains("search_work_items", names);
        Assert.Contains("list_project_areas", names);
        Assert.Contains("list_my_work_items", names);
        Assert.Contains("list_items_mentioning_me", names);
        Assert.Contains("list_saved_queries", names);
        Assert.Contains("execute_saved_query", names);
        Assert.Contains("list_pull_requests_for_my_review", names);
        Assert.Contains("list_my_recent_activity", names);
        Assert.Contains("correlate_work_item_with_github", names);
        Assert.Contains("get_work_item", names);
        Assert.Contains("add_work_item_comment", names);
        Assert.Contains("list_github_issues", names);
        Assert.Contains("get_github_issue", names);
    }

    private static string FindDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "dotnet",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(local) ? local : "dotnet";
    }
}
