using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Conversations;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Cli;

public static class Program
{
    public static string Version =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"workmonitor {Version}");
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

        builder.Services.AddSingleton(
            new SessionStore(Path.Combine(dataDirectory, "workmonitor.db")));
        builder.Services.AddSingleton(
            new ParametersService(dataDirectory, builder.Configuration));
        builder.Services.AddSingleton(userCredential);
        builder.Services.AddSingleton<TokenCredential>(
            services => services.GetRequiredService<PersistentInteractiveCredential>());
        builder.Services.AddSingleton<DevOpsTokenService>();
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
        builder.Services.AddSingleton<ConsoleWriteApprovalHandler>();
        builder.Services.AddSingleton<IWriteApprovalHandler>(
            services => services.GetRequiredService<ConsoleWriteApprovalHandler>());
        builder.Services.AddSingleton<AzureOpenAIService>();
        builder.Services.AddSingleton<IAgentModel>(
            services => services.GetRequiredService<AzureOpenAIService>());
        builder.Services.AddSingleton<AgentService>();
        builder.Services.AddSingleton<ConversationCompactor>();
        builder.Services.AddSingleton<CliApplication>();

        using var host = builder.Build();

        if (args.Contains("--auth-reset", StringComparer.OrdinalIgnoreCase))
        {
            await userCredential.ResetAsync();
            Console.WriteLine("Saved WorkMonitor account selection was cleared.");
            return 0;
        }

        if (args.Contains("--auth-login", StringComparer.OrdinalIgnoreCase))
        {
            var record = await userCredential.AuthenticateAsync(force: true);
            Console.WriteLine(
                $"Authenticated as {record.Username} in tenant {record.TenantId}.");
            return 0;
        }

        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
        {
            return await CheckConfigurationAsync(
                host.Services.GetRequiredService<SessionStore>(),
                userCredential,
                builder.Configuration);
        }

        if (!HasModelConfiguration(builder.Configuration, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Configure AzureOpenAI__Endpoint and AzureOpenAI__DeploymentName. " +
                "AzureOpenAI__ApiKey is optional when your Azure identity has model access.");
            return 2;
        }

        return await host.Services.GetRequiredService<CliApplication>().RunAsync(args);
    }

    private static async Task<int> CheckConfigurationAsync(
        SessionStore sessionStore,
        PersistentInteractiveCredential credential,
        IConfiguration configuration)
    {
        await sessionStore.InitializeAsync();
        var configured = HasModelConfiguration(configuration, out var error);

        Console.WriteLine($"Session database: {sessionStore.DatabasePath}");
        Console.WriteLine(
            $"Azure endpoint: {(string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"]) ? "missing" : "configured")}");
        Console.WriteLine(
            $"Deployment: {configuration["AzureOpenAI:DeploymentName"] ?? "missing"}");
        Console.WriteLine(
            $"Authentication: {(string.IsNullOrWhiteSpace(configuration["AzureOpenAI:ApiKey"]) ? "Azure identity" : "API key")}");
        var cachedUsername = await credential.GetCachedUsernameAsync();
        Console.WriteLine(
            $"Saved identity: {cachedUsername ?? "none; browser sign-in required"}");

        if (!configured)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        Console.WriteLine("Configuration check passed.");
        return 0;
    }

    private static bool HasModelConfiguration(
        IConfiguration configuration,
        out string error)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration["AzureOpenAI:Endpoint"]))
        {
            missing.Add("AzureOpenAI:Endpoint");
        }

        if (string.IsNullOrWhiteSpace(configuration["AzureOpenAI:DeploymentName"]))
        {
            missing.Add("AzureOpenAI:DeploymentName");
        }

        error = missing.Count == 0
            ? string.Empty
            : $"Missing configuration: {string.Join(", ", missing)}.";
        return missing.Count == 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            workmonitor - conversational Azure DevOps and GitHub triage

            Usage:
              workmonitor
              workmonitor --prompt "question"
              workmonitor --check
              workmonitor --auth-login
              workmonitor --auth-reset
              workmonitor --version

            Configuration:
              AzureOpenAI__Endpoint
              AzureOpenAI__DeploymentName
              AzureOpenAI__ApiKey          optional; Azure identity is used when omitted
              GitHub__Token                optional for public repositories
              WorkMonitor__Authentication__TenantId
              WorkMonitor__Authentication__LoginHint

            Interactive commands:
              /new                         start a new conversation
              /sessions                    list saved conversations
              /resume <session-id>         resume a saved conversation
              /model                       show the active deployment
              /tools                       list available tools
              /audit                       show write audit entries
              /working-set                 show pinned work items
              /working-set add <id> [type] pin a work item
              /working-set clear           clear pinned work items
              /dry-run                     prevent write operations
              /live                        require approval for write operations
              /help                        show commands
              /exit                        exit
            """);
    }
}
