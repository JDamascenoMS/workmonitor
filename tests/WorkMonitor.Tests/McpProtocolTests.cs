using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WorkMonitor.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task StdioServerInitializesAndListsSharedTools()
    {
        var serverPath = FindServerAssembly();
        Assert.True(
            File.Exists(serverPath),
            $"MCP server assembly was not found at '{serverPath}'.");

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "WorkMonitor test server",
                Command = FindDotnetHost(),
                Arguments = new[] { serverPath },
                WorkingDirectory = Path.GetDirectoryName(serverPath)
            });
        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync(
            cancellationToken: CancellationToken.None);
        var names = tools.Select(tool => tool.Name).ToHashSet(
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(15, names.Count);
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
        Assert.Contains("update_work_item", names);
        Assert.Contains("create_bug_from_feedback", names);
        Assert.Contains("list_github_issues", names);
        Assert.Contains("get_github_issue", names);

        var update = tools.Single(tool => tool.Name == "update_work_item");
        var properties = update.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("expectedRevision", out _));
        var fieldProperties = properties.GetProperty("fields").GetProperty("properties");
        Assert.Equal(
            new[] { "assignedTo", "description", "priority", "state", "tags", "title" },
            fieldProperties.EnumerateObject().Select(property => property.Name).OrderBy(name => name));

        var preview = await client.CallToolAsync(
            "update_work_item",
            new Dictionary<string, object?>
            {
                ["id"] = 42,
                ["expectedRevision"] = 7,
                ["fields"] = new { priority = 2 }
            },
            cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, preview.IsError);
        var result = JsonSerializer.Deserialize<JsonElement>(
            Assert.IsType<TextContentBlock>(Assert.Single(preview.Content)).Text);
        Assert.True(result.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, result.GetProperty("fields").GetProperty("Microsoft.VSTS.Common.Priority").GetInt32());

        var invalid = await client.CallToolAsync(
            "update_work_item",
            new Dictionary<string, object?>
            {
                ["id"] = 42,
                ["expectedRevision"] = 7,
                ["fields"] = new { priority = -1 }
            },
            cancellationToken: CancellationToken.None);
        Assert.True(invalid.IsError);
        Assert.Contains("priority must be nonnegative",
            Assert.IsType<TextContentBlock>(Assert.Single(invalid.Content)).Text);

        var unknownField = await client.CallToolAsync(
            "update_work_item",
            new Dictionary<string, object?>
            {
                ["id"] = 42,
                ["expectedRevision"] = 7,
                ["fields"] = new { priority = 2, areaPath = "Not allowed" }
            },
            cancellationToken: CancellationToken.None);
        Assert.True(unknownField.IsError);

        var creator = tools.Single(tool => tool.Name == "create_bug_from_feedback");
        var bugSchema = creator.JsonSchema.GetProperty("properties").GetProperty("bug");
        Assert.Equal(
            new[] { "areaPath", "reproSteps", "title" },
            bugSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).OrderBy(name => name));
        var createPreview = await client.CallToolAsync(
            creator.Name,
            new Dictionary<string, object?>
            {
                ["feedbackTicketId"] = 42,
                ["bug"] = new { title = "Fixture Bug", areaPath = @"DevDiv\NET Tools\Project", reproSteps = "<p>Fixture steps</p>", priority = 0 }
            },
            cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, createPreview.IsError);
        var creation = JsonSerializer.Deserialize<JsonElement>(
            Assert.IsType<TextContentBlock>(Assert.Single(createPreview.Content)).Text);
        Assert.True(creation.GetProperty("dryRun").GetBoolean());
        Assert.Equal("Bug", creation.GetProperty("workItemType").GetString());
        Assert.Equal(0, creation.GetProperty("fields").GetProperty("Microsoft.VSTS.Common.Priority").GetInt32());
        Assert.Equal("System.LinkTypes.Related", creation.GetProperty("relation").GetProperty("rel").GetString());
        Assert.EndsWith("/workitems/42", creation.GetProperty("relation").GetProperty("url").GetString());

        var invalidSource = await client.CallToolAsync(
            creator.Name,
            new Dictionary<string, object?>
            {
                ["feedbackTicketId"] = 0,
                ["bug"] = new { title = "Fixture Bug", areaPath = "DevDiv", reproSteps = "Steps" }
            },
            cancellationToken: CancellationToken.None);
        Assert.True(invalidSource.IsError);
        Assert.Contains("feedbackTicketId must be positive",
            Assert.IsType<TextContentBlock>(Assert.Single(invalidSource.Content)).Text);

        var missingRepro = await client.CallToolAsync(
            creator.Name,
            new Dictionary<string, object?>
            {
                ["feedbackTicketId"] = 42,
                ["bug"] = new { title = "Fixture Bug", areaPath = "DevDiv", description = "Not Repro Steps" }
            },
            cancellationToken: CancellationToken.None);
        Assert.True(missingRepro.IsError);

        var arbitraryFields = await client.CallToolAsync(
            creator.Name,
            new Dictionary<string, object?>
            {
                ["feedbackTicketId"] = 42,
                ["bug"] = new { title = "Fixture Bug", areaPath = "DevDiv", reproSteps = "Steps", state = "Closed" }
            },
            cancellationToken: CancellationToken.None);
        Assert.True(arbitraryFields.IsError);
    }

    private static string FindServerAssembly()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Parent?.Name
            ?? throw new InvalidOperationException("Test build configuration could not be determined.");
        var directory = output;
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "FT-MONITOR.sln")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
            throw new InvalidOperationException("The solution directory could not be located.");

        return Path.Combine(
            directory.FullName, "src", "WorkMonitor.Mcp", "bin",
            configuration, output.Name, "workmonitor-mcp.dll");
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
