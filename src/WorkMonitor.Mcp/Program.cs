using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Reflection;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Mcp;

public static class Program
{
    public static string Version =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"workmonitor-mcp {Version}");
            return 0;
        }

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                """
                workmonitor-mcp - WorkMonitor tools over Model Context Protocol

                Usage:
                  workmonitor-mcp
                  workmonitor-mcp --list-tools
                  workmonitor-mcp --version

                The default transport is stdio. Writes remain dry-run unless both
                WorkMonitor__Mcp__AllowWrites=true and confirm=true are supplied.
                """);
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(typeof(Program).Assembly, optional: true)
            .AddEnvironmentVariables();

        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        var dataDirectory = builder.Configuration["WorkMonitor:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkMonitor");
        }

        var tenantId = builder.Configuration[
            "WorkMonitor:Authentication:TenantId"];
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException(
                "WorkMonitor:Authentication:TenantId is required.");
        }

        var userCredential = new PersistentInteractiveCredential(
            new PersistentInteractiveCredentialOptions(
                tenantId,
                builder.Configuration["WorkMonitor:Authentication:LoginHint"],
                "WorkMonitor",
                Path.Combine(dataDirectory, "authentication-record.json")));
        builder.Services.AddSingleton(userCredential);
        builder.Services.AddSingleton<TokenCredential>(
            services => services.GetRequiredService<PersistentInteractiveCredential>());
        builder.Services.AddSingleton<DevOpsTokenService>();
        builder.Services.AddSingleton(
            new ParametersService(dataDirectory, builder.Configuration));
        builder.Services.AddHttpClient<GitHubClient>();
        builder.Services.AddSingleton<IAgentTool, SearchWorkItemsTool>();
        builder.Services.AddSingleton<IAgentTool, GetWorkItemTool>();
        builder.Services.AddSingleton<IAgentTool, AddWorkItemCommentTool>();
        builder.Services.AddSingleton<IAgentTool, ListProjectAreasTool>();
        builder.Services.AddSingleton<IAgentTool, ListMyWorkItemsTool>();
        builder.Services.AddSingleton<IAgentTool, ListMyMentionedItemsTool>();
        builder.Services.AddSingleton<IAgentTool, ListSavedQueriesTool>();
        builder.Services.AddSingleton<IAgentTool, ExecuteSavedQueryTool>();
        builder.Services.AddSingleton<IAgentTool, ListPullRequestsForReviewTool>();
        builder.Services.AddSingleton<IAgentTool, ListMyRecentActivityTool>();
        builder.Services.AddSingleton<IAgentTool, CorrelateWorkItemWithGitHubTool>();
        builder.Services.AddSingleton<IAgentTool, ListGithubIssuesTool>();
        builder.Services.AddSingleton<IAgentTool, GetGithubIssueTool>();
        builder.Services.AddSingleton<AgentToolRegistry>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<WorkMonitorMcpTools>();

        using var host = builder.Build();

        if (args.Contains("--list-tools", StringComparer.OrdinalIgnoreCase))
        {
            var registry = host.Services.GetRequiredService<AgentToolRegistry>();
            foreach (var tool in registry.All.OrderBy(tool => tool.Name))
            {
                Console.WriteLine(tool.Name);
            }
            return 0;
        }

        await host.RunAsync();
        return 0;
    }
}
