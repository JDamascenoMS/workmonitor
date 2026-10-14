using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using FT_MONITOR.Middleware;
using FT_MONITOR.Services;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Conversations;
using WorkMonitor.Agent.Tools;
using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Services;

namespace FT_MONITOR
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var authenticationMode = WebAuthenticationModeConfiguration.GetMode(builder.Configuration);
            var localDevelopment = authenticationMode == WebAuthenticationMode.LocalDevelopment;
            var configuredDataDirectory = builder.Configuration["WorkMonitor:Web:DataDirectory"];
            var webDataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkMonitor",
                    "Web")
                : Path.GetFullPath(configuredDataDirectory);

            // Forward headers from Azure App Service reverse proxy.
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                         | ForwardedHeaders.XForwardedProto;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });

            if (localDevelopment)
            {
                var urls = builder.Configuration["urls"] ??
                    "https://localhost:7286;http://localhost:5191";
                LocalDevelopmentOnlyMiddleware.ValidateStartup(
                    builder.Environment, builder.Configuration, urls);
                builder.WebHost.UseUrls(urls);
                LocalDevelopmentAuthentication.Register(
                    builder.Services, builder.Configuration, webDataDirectory);
            }
            else
            {
                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
                builder.Services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser()
                        .RequireAssertion(context =>
                            WebApiAuthentication.HasDelegatedScope(context.User))
                        .Build();
                });
                builder.Services.AddHttpClient<IWebDevOpsTokenService, WebDevOpsTokenService>()
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            }

            builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
            var mvc = builder.Services.AddControllersWithViews(options =>
            {
                if (localDevelopment)
                    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            });
            if (localDevelopment)
                mvc.AddMicrosoftIdentityUI();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSingleton<AzureOpenAIService>();
            builder.Services.AddSingleton<IAgentModel>(
                services => services.GetRequiredService<AzureOpenAIService>());
            builder.Services.AddSingleton(
                _ => new ParametersService(
                    builder.Environment.ContentRootPath,
                    builder.Configuration));
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(
                new SessionStore(Path.Combine(webDataDirectory, "conversations.db")));
            builder.Services.AddHostedService<WebConversationStoreInitializer>();
            builder.Services.AddSingleton<ConversationTurnCoordinator>();
            builder.Services.AddSingleton<ConversationCompactor>();
            builder.Services.AddHttpClient<GitHubClient>();

            builder.Services.AddSingleton<IAgentTool, SearchWorkItemsTool>();
            builder.Services.AddSingleton<IAgentTool, GetWorkItemTool>();
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
            builder.Services.AddSingleton<IWriteApprovalHandler, DenyWriteApprovalHandler>();
            builder.Services.AddSingleton<AgentService>();
            builder.Services.AddSingleton(
                _ => new SkillStore(builder.Environment.ContentRootPath));

            var app = builder.Build();
            if (localDevelopment)
                app.UseMiddleware<LocalDevelopmentOnlyMiddleware>();
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            if (!localDevelopment)
                app.UseForwardedHeaders();
            if (app.Environment.IsDevelopment())
                app.UseHttpsRedirection();

            app.UseMiddleware<WebAuthenticationErrorMiddleware>();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    if (context.Context.Request.Path.Equals(
                        "/auth-callback.html", StringComparison.OrdinalIgnoreCase) ||
                        context.Context.Request.Path.Equals(
                            "/signin-popup-complete.html", StringComparison.OrdinalIgnoreCase))
                        context.Context.Response.Headers.CacheControl = "no-store";
                }
            });
            app.MapControllers();
            app.MapGet("/api/auth/status", (IConfiguration configuration, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(WebApiAuthentication.GetBrowserConfiguration(configuration));
            });
            app.MapGet("/api/me", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                if (ctx.User.Identity?.IsAuthenticated != true)
                    return Results.Unauthorized();
                var developerIdentity = ctx.User.FindFirst("preferred_username")?.Value ??
                    ctx.User.Identity.Name;
                if (localDevelopment && string.IsNullOrWhiteSpace(developerIdentity))
                    developerIdentity = WebConversationIdentity.GetOwnerId(ctx.User);

                return Results.Ok(new
                {
                    name = ctx.User.FindFirst("name")?.Value
                           ?? ctx.User.Identity.Name
                           ?? "Unknown",
                    email = ctx.User.FindFirst("preferred_username")?.Value ?? "",
                    authMode = localDevelopment ? "localDevelopment" : "spaPkce",
                    devOpsIdentity = localDevelopment ? developerIdentity : null
                });
            }).RequireAuthorization();

            app.Run();
        }
    }
}
