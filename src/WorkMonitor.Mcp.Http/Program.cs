using Microsoft.AspNetCore.HostFiltering;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Tools;
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
        builder.Services.AddScoped<IDevOpsOboTokenAcquirer, DevOpsOboTokenAcquirer>();
        builder.Services.AddHttpClient("EntraObo").RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddHttpClient("DevOps").RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        builder.Services.AddSingleton(provider => new ParametersService(
            provider.GetRequiredService<HttpMcpSettings>().DataDirectory, builder.Configuration));
        builder.Services.AddSingleton<IAgentTool>(provider => new GetWorkItemTool(async (context, token) =>
            new MonitorService(await context.GetRequiredDevOpsTokenAsync(token),
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("DevOps"))));
        builder.Services.AddSingleton<IAgentTool, ListSavedQueriesTool>();
        builder.Services.AddSingleton<IAgentTool, ListProjectAreasTool>();
        builder.Services.AddSingleton<AgentToolRegistry>();
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
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
