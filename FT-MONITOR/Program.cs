using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

namespace FT_MONITOR
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Forward headers from Azure App Service reverse proxy
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                         | ForwardedHeaders.XForwardedProto;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });

            // Persist data-protection keys to disk so the auth cookie remains
            // decryptable across app restarts/rebuilds in development. Without
            // this, every rebuild rotates the in-memory keys and the browser's
            // existing .AspNetCore.Cookies cookie can no longer be decrypted —
            // resulting in silent 401s on /api/* until the user clears cookies.
            var keysFolder = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DpKeys");
            Directory.CreateDirectory(keysFolder);
            builder.Services.AddDataProtection()
                .SetApplicationName("FT-MONITOR")
                .PersistKeysToFileSystem(new DirectoryInfo(keysFolder));

            // Add Microsoft Entra ID authentication for page protection (OIDC sign-in).
            // Azure DevOps tokens are acquired separately via DefaultAzureCredential
            // (managed identity in Azure, VS/CLI locally) — no admin consent needed.
            //
            // Scheme topology:
            //   - Default authenticate / sign-in scheme = Cookies
            //         (every request reads the session cookie to populate User)
            //   - Default challenge scheme = OpenIdConnect
            //         (when an unauthenticated request needs to sign in,
            //          the OIDC handler redirects to Microsoft Entra ID)
            //
            // Forcing OIDC as the default authenticate scheme breaks API calls
            // because the OIDC handler can't authenticate a normal request from
            // a cookie — it only validates the inbound auth code response.
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
                .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

            // If a cookie can't be decrypted (e.g. data-protection keys changed)
            // or the principal is otherwise invalid, sign the user out so the
            // next page navigation triggers a fresh OIDC sign-in instead of an
            // endless 401 loop.
            builder.Services.Configure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme, options =>
                {
                    options.Events.OnValidatePrincipal = async ctx =>
                    {
                        if (ctx.Principal?.Identity?.IsAuthenticated != true)
                        {
                            ctx.RejectPrincipal();
                            await ctx.HttpContext.SignOutAsync(
                                CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    };
                });

            // For /api/* requests, return 401 instead of redirecting to the login page.
            // Browser fetch() cannot follow a cross-origin redirect to login.microsoftonline.com,
            // so we let the client JS detect 401 and trigger a full-page sign-in navigation.
            builder.Services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    var originalOnRedirect = options.Events.OnRedirectToIdentityProvider;
                    options.Events.OnRedirectToIdentityProvider = async ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            ctx.HandleResponse();
                            return;
                        }
                        if (originalOnRedirect != null)
                            await originalOnRedirect(ctx);
                    };
                });

            builder.Services.AddAuthorization();

            builder.Services.AddControllersWithViews()
                .AddMicrosoftIdentityUI();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSingleton<FT_MONITOR.Services.AzureOpenAIService>();
            builder.Services.AddSingleton<FT_MONITOR.Services.ParametersService>();
            builder.Services.AddSingleton<FT_MONITOR.Services.DevOpsTokenService>();

            // Typed HttpClient for the GitHub REST API used by the
            // GitHub Issues dashboard. Token (if any) comes from GitHub:Token.
            builder.Services.AddHttpClient<FT_MONITOR.Clients.GitHubClient>();

            // ---- Agent feature ----
            // Tools are registered as singletons through IAgentTool so the
            // AgentToolRegistry can collect them via DI. Add new tools here.
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.IAgentTool, FT_MONITOR.Services.Agent.Tools.SearchWorkItemsTool>();
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.IAgentTool, FT_MONITOR.Services.Agent.Tools.GetWorkItemTool>();
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.IAgentTool, FT_MONITOR.Services.Agent.Tools.AddWorkItemCommentTool>();
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.IAgentTool, FT_MONITOR.Services.Agent.Tools.ListProjectAreasTool>();
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.IAgentTool, FT_MONITOR.Services.Agent.Tools.ListGithubIssuesTool>();
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.IAgentTool, FT_MONITOR.Services.Agent.Tools.GetGithubIssueTool>();
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.AgentToolRegistry>();
            builder.Services.AddSingleton<FT_MONITOR.Services.Agent.AgentService>();
            builder.Services.AddSingleton<FT_MONITOR.Services.SkillStore>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseForwardedHeaders();

            // App Service handles HTTPS at the platform level; only redirect locally
            if (app.Environment.IsDevelopment())
            {
                app.UseHttpsRedirection();
            }

            app.UseAuthentication();
            app.UseAuthorization();

            // NOTE: HTML pages are intentionally NOT guarded server-side anymore.
            // Pages load for everyone, and the client-side renderUserInfo() helper
            // (in /js/msal-auth.js) shows either the signed-in user or a "Sign in"
            // link that opens an OIDC popup. /api/* endpoints remain protected
            // via [Authorize] / RequireAuthorization() and return 401 to anon users.

            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapControllers();

            // Expose the signed-in user info so the frontend can display it
            app.MapGet("/api/me", (HttpContext ctx) =>
            {
                if (ctx.User.Identity?.IsAuthenticated != true)
                    return Results.Unauthorized();

                return Results.Ok(new
                {
                    name = ctx.User.FindFirst("name")?.Value
                           ?? ctx.User.Identity.Name
                           ?? "Unknown",
                    email = ctx.User.FindFirst("preferred_username")?.Value ?? ""
                });
            }).RequireAuthorization();

            app.Run();
        }
    }
}
