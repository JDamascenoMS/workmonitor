using System.Text.Json;
using System.Diagnostics;
using System.IO.Pipelines;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Services;
using WorkMonitor.Mcp;

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

        using var directory = new TestDataDirectory("mcp-protocol-data");
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "WorkMonitor test server",
                Command = FindDotnetHost(),
                Arguments = new[] { serverPath },
                WorkingDirectory = Path.GetDirectoryName(serverPath),
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["WorkMonitor__DataDirectory"] = directory.Path,
                    ["AzureOpenAI__Endpoint"] = "",
                    ["AzureOpenAI__DeploymentName"] = "",
                    ["AzureOpenAI__ApiKey"] = ""
                }
            });
        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync(
            cancellationToken: CancellationToken.None);
        var names = tools.Select(tool => tool.Name).ToHashSet(
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(16, names.Count);
        Assert.Contains("search_work_items", names);
        Assert.Contains("list_project_areas", names);
        Assert.Contains("list_my_work_items", names);
        Assert.Contains("list_items_mentioning_me", names);
        Assert.Contains("list_saved_queries", names);
        Assert.Contains("execute_saved_query", names);
        Assert.Contains("analyze_feedback_themes", names);
        Assert.Contains("list_pull_requests_for_my_review", names);
        Assert.Contains("list_my_recent_activity", names);
        Assert.Contains("correlate_work_item_with_github", names);
        Assert.Contains("get_work_item", names);
        Assert.Contains("add_work_item_comment", names);
        Assert.Contains("update_work_item", names);
        Assert.Contains("create_bug_from_feedback", names);
        Assert.Contains("list_github_issues", names);
        Assert.Contains("get_github_issue", names);
        var themes = tools.Single(tool => tool.Name == "analyze_feedback_themes");
        Assert.True(themes.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal(
            new[] { "maxResults", "projectArea", "query" },
            themes.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        var invalidThemes = await client.CallToolAsync(
            themes.Name,
            new Dictionary<string, object?> { ["projectArea"] = "Project", ["query"] = FeedbackThemeTests.QueryId },
            cancellationToken: CancellationToken.None);
        Assert.True(invalidThemes.IsError);
        Assert.Contains("not both", Assert.IsType<TextContentBlock>(Assert.Single(invalidThemes.Content)).Text);
        var areas = await client.CallToolAsync(
            "list_project_areas", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, areas.IsError);
        var lookupSchema = tools.Single(tool => tool.Name == "get_work_item").JsonSchema;
        Assert.Equal("detail", lookupSchema.GetProperty("properties").GetProperty("view").GetProperty("default").GetString());
        var invalidLookup = await client.CallToolAsync(
            "get_work_item", new Dictionary<string, object?> { ["id"] = 42, ["view"] = "all" },
            cancellationToken: CancellationToken.None);
        Assert.True(invalidLookup.IsError);
        Assert.Contains("view must be summary or detail", Assert.IsType<TextContentBlock>(Assert.Single(invalidLookup.Content)).Text);

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

    [Fact]
    public async Task ThemeToolReturnsGroundedResultAndExplicitModelErrorsOverMcpProtocol()
    {
        using var directory = new TestDataDirectory("mcp-theme-data");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var parameters = new ParametersService(directory.Path);
        parameters.AddSavedQuery("FTMonitor - FTS", FeedbackThemeTests.QueryId);
        var model = new ThemeClassifier();
        using var handler = new ThemeSourceHandler([1, 2]);
        var modelConfigured = true;
        var analysis = new FeedbackThemeAnalysisService(parameters, () => modelConfigured
            ? model
            : new AzureOpenAIService(new ConfigurationBuilder().Build(), new FixtureCredential()));
        var tool = new AnalyzeFeedbackThemesTool(analysis, async (context, token) =>
            new MonitorClient(await context.GetRequiredDevOpsTokenAsync(token), new HttpClient(handler)));
        using var lookupSource = new LookupSourceHandler { Id = 1 };
        var lookup = WorkItemLookupTests.Tool(lookupSource);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(new AgentToolRegistry(new IAgentTool[] { tool, lookup }));
        builder.Services.AddSingleton(new DevOpsTokenService(new FixtureCredential()));
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<WorkMonitorMcpTools>();
        using var host = builder.Build();
        try
        {
            await host.StartAsync(timeout.Token);
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var schema = tools.Single(item => item.Name == tool.Name).JsonSchema;
            Assert.Equal(20, schema.GetProperty("properties").GetProperty("maxResults").GetProperty("default").GetInt32());
            var response = await client.CallToolAsync(tool.Name,
                new Dictionary<string, object?> { ["query"] = "FTMonitor - FTS", ["maxResults"] = 20 },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, response.IsError);
            var result = JsonSerializer.Deserialize<JsonElement>(
                Assert.IsType<TextContentBlock>(Assert.Single(response.Content)).Text);
            Assert.Equal("Reload", Assert.Single(result.GetProperty("themes").EnumerateArray()).GetProperty("theme").GetString());
            Assert.Equal("FTMonitor - FTS", result.GetProperty("source").GetProperty("query").GetString());
            Assert.Equal(2, result.GetProperty("coverage").GetProperty("analyzedCount").GetInt32());
            Assert.Equal(new[] { 2 }, result.GetProperty("coverage").GetProperty("unclassifiedWorkItemIds").EnumerateArray().Select(id => id.GetInt32()));
            Assert.All(result.GetProperty("items").EnumerateArray(), item =>
                Assert.Contains("/_workitems/edit/", item.GetProperty("url").GetString()));
            var lookupResponse = await client.CallToolAsync("get_work_item",
                new Dictionary<string, object?> { ["id"] = 1, ["view"] = "summary" },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, lookupResponse.IsError);
            var ticket = JsonSerializer.Deserialize<JsonElement>(
                Assert.IsType<TextContentBlock>(Assert.Single(lookupResponse.Content)).Text);
            WorkItemLookupTests.AssertLookup(ticket, 1);
            Assert.Equal(result.GetProperty("items")[0].GetProperty("url").GetString(), ticket.GetProperty("url").GetString());
            Assert.Single(lookupSource.Paths);
            Assert.False(ticket.TryGetProperty("description", out _));
            var detailResponse = await client.CallToolAsync("get_work_item",
                new Dictionary<string, object?> { ["id"] = 1 },
                cancellationToken: timeout.Token);
            Assert.NotEqual(true, detailResponse.IsError);
            var detail = JsonSerializer.Deserialize<JsonElement>(
                Assert.IsType<TextContentBlock>(Assert.Single(detailResponse.Content)).Text);
            Assert.Equal("engineer@example.com", detail.GetProperty("assignedTo").GetProperty("uniqueName").GetString());
            Assert.Equal("detail", detail.GetProperty("coverage").GetProperty("view").GetString());
            lookupSource.ItemStatus = System.Net.HttpStatusCode.Forbidden;
            lookupSource.ItemJson = "DENIED-TICKET-CONTENT";
            var denied = await client.CallToolAsync("get_work_item",
                new Dictionary<string, object?> { ["id"] = 1, ["view"] = "summary" },
                cancellationToken: timeout.Token);
            Assert.True(denied.IsError);
            var deniedText = Assert.IsType<TextContentBlock>(Assert.Single(denied.Content)).Text;
            Assert.Contains("Verify the id and your Azure DevOps access", deniedText);
            Assert.DoesNotContain("DENIED-TICKET-CONTENT", deniedText);
            model.Output = """[{"theme":"Invented","description":"No source","workItemIds":[99],"count":1}]""";
            var invalid = await client.CallToolAsync(tool.Name,
                new Dictionary<string, object?> { ["query"] = "FTMonitor - FTS" },
                cancellationToken: timeout.Token);
            Assert.True(invalid.IsError);
            Assert.Contains("outside the analyzed source", Assert.IsType<TextContentBlock>(Assert.Single(invalid.Content)).Text);
            modelConfigured = false;
            var missingConfiguration = await client.CallToolAsync(tool.Name,
                new Dictionary<string, object?> { ["query"] = "FTMonitor - FTS" },
                cancellationToken: timeout.Token);
            Assert.True(missingConfiguration.IsError);
            Assert.Contains("AzureOpenAI:Endpoint is not configured",
                Assert.IsType<TextContentBlock>(Assert.Single(missingConfiguration.Content)).Text);
            lookupSource.ItemStatus = System.Net.HttpStatusCode.OK;
            lookupSource.ItemJson = """{"value":[]}""";
            var missing = await client.CallToolAsync("get_work_item",
                new Dictionary<string, object?> { ["id"] = 1, ["view"] = "summary" },
                cancellationToken: timeout.Token);
            Assert.True(missing.IsError);
            Assert.Contains("not found or is not accessible", Assert.IsType<TextContentBlock>(Assert.Single(missing.Content)).Text);
            lookupSource.ItemJson = """{"value":[{"id":99}]}""";
            var malformed = await client.CallToolAsync("get_work_item",
                new Dictionary<string, object?> { ["id"] = 1 },
                cancellationToken: timeout.Token);
            Assert.True(malformed.IsError);
            Assert.Contains("malformed work-item data", Assert.IsType<TextContentBlock>(Assert.Single(malformed.Content)).Text);
        }
        finally
        {
            await host.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task CliRegistersThemeToolWithoutInvokingModelOrDevOps()
    {
        var serverPath = FindServerAssembly();
        var cliPath = serverPath
            .Replace("WorkMonitor.Mcp", "WorkMonitor.Cli", StringComparison.Ordinal)
            .Replace("workmonitor-mcp.dll", "workmonitor.dll", StringComparison.Ordinal);
        using var directory = new TestDataDirectory("cli-theme-data");
        var start = new ProcessStartInfo(FindDotnetHost())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(cliPath)!
        };
        start.ArgumentList.Add(cliPath);
        start.Environment["WorkMonitor__DataDirectory"] = directory.Path;
        start.Environment["AzureOpenAI__Endpoint"] = "https://example.invalid/";
        start.Environment["AzureOpenAI__DeploymentName"] = "fixture";
        start.Environment["AzureOpenAI__ApiKey"] = "fixture-no-network";
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("/tools");
            await process.StandardInput.WriteLineAsync("/exit");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("analyze_feedback_themes", await output);
            Assert.DoesNotContain("Model call failed", await errors);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private sealed class TestDataDirectory(string category) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            AppContext.BaseDirectory, category, Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FixtureCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
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
