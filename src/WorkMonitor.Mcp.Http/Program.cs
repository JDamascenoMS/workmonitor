using Microsoft.AspNetCore.HostFiltering;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Mcp.Http;

public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(provider =>
            HttpMcpSettings.Load(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddHostFiltering(_ => { });
        builder.Services.AddOptions<HostFilteringOptions>().Configure<HttpMcpSettings>((options, settings) =>
        {
            options.AllowedHosts = settings.AllowedHosts.ToList();
            options.AllowEmptyHosts = false;
            options.IncludeFailureMessage = false;
        });
        builder.Services.AddHttpMcpAuthentication();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<RequestDevOpsToken>();
        builder.Services.AddScoped<RequestThemeAnalysis>();
        builder.Services.AddSingleton<HttpThemePolicy>();
        builder.Services.AddSingleton<HttpThemeModel>();
        builder.Services.AddSingleton<IFeedbackThemeClassifier>(provider => provider.GetRequiredService<HttpThemeModel>());
        builder.Services.AddScoped<IDevOpsOboTokenAcquirer, DevOpsOboTokenAcquirer>();
        builder.Services.AddHttpClient("EntraObo").RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddHttpClient("DevOps").RemoveAllLoggers()
            .AddHttpMessageHandler(() => new BoundedHttpResponseHandler(2 * 1024 * 1024))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddHttpClient("ThemeModel").RemoveAllLoggers()
            .AddHttpMessageHandler(() => new BoundedHttpResponseHandler(512 * 1024))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddHttpClient("ModelIdentity").RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddSingleton(provider => new ParametersService(
            provider.GetRequiredService<HttpMcpSettings>().DataDirectory, builder.Configuration));
        builder.Services.AddSingleton<IAgentTool>(provider => new GetWorkItemTool(async (context, token) =>
            new MonitorService(await context.GetRequiredDevOpsTokenAsync(token),
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("DevOps"))));
        builder.Services.AddSingleton<IAgentTool, ListSavedQueriesTool>();
        builder.Services.AddSingleton<IAgentTool, ListProjectAreasTool>();
        builder.Services.AddSingleton(provider => new FeedbackThemeAnalysisService(
            provider.GetRequiredService<ParametersService>(),
            () => provider.GetRequiredService<IFeedbackThemeClassifier>()));
        builder.Services.AddSingleton(provider => new AnalyzeFeedbackThemesTool(
            provider.GetRequiredService<FeedbackThemeAnalysisService>(), async (context, cancellationToken) =>
                new MonitorClient(await context.GetRequiredDevOpsTokenAsync(cancellationToken),
                    provider.GetRequiredService<IHttpClientFactory>().CreateClient("DevOps"))));
        builder.Services.AddSingleton<IAgentTool>(provider => provider.GetRequiredService<AnalyzeFeedbackThemesTool>());
        builder.Services.AddSingleton<AgentToolRegistry>();
        builder.Services.AddMcpServer().WithHttpTransport(options =>
        {
            options.Stateless = true;
            // The SDK validates transport headers before this stateless per-request hook, and starts SSE after it.
            options.ConfigureSessionOptions = (context, _, _) =>
                context.RequestServices.GetRequiredService<RequestThemeAnalysis>().PrepareAsync();
        })
            .WithTools<HttpReadTools>();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);

        var app = builder.Build();
        _ = app.Services.GetRequiredService<HttpMcpSettings>();
        app.UseHostFiltering();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<HttpMcpRequestMiddleware>();
        app.MapMcp("/mcp").RequireAuthorization(HttpMcpAuthentication.Policy);
        app.Run();
    }
}
