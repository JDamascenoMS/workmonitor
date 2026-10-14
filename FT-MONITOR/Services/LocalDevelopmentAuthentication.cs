using System.Security.Claims;
using Azure.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using WorkMonitor.Core.Services;

namespace FT_MONITOR.Services;

public static class LocalDevelopmentAuthentication
{
    public const string ModeClaim = "workmonitor_auth_mode";
    public const string ModeValue = "localDevelopment";

    public static void Register(
        IServiceCollection services, IConfiguration configuration, string webDataDirectory)
    {
        var tenant = configuration["WorkMonitor:Authentication:TenantId"] ??
            configuration["AzureAd:TenantId"];
        var loginHint = configuration["WorkMonitor:Authentication:LoginHint"];
        if (!Guid.TryParse(tenant, out var tenantId) ||
            !Guid.TryParse(configuration["AzureAd:TenantId"], out var webTenant) ||
            tenantId == Guid.Empty || tenantId != webTenant ||
            string.IsNullOrWhiteSpace(loginHint))
            throw new InvalidOperationException(
                "LocalDevelopment requires a matching AzureAd/WorkMonitor tenant and WorkMonitor:Authentication:LoginHint for the developer.");
        var dataDirectory = configuration["WorkMonitor:DataDirectory"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkMonitor");
        var keysDirectory = Path.Combine(webDataDirectory, "LocalDpKeys");
        Directory.CreateDirectory(keysDirectory);
        var dataProtection = services.AddDataProtection().SetApplicationName("WorkMonitor.LocalDevelopment")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
        if (OperatingSystem.IsWindows())
            dataProtection.ProtectKeysWithDpapi();
        services.AddSingleton(new PersistentInteractiveCredential(
            new PersistentInteractiveCredentialOptions(
                tenantId.ToString("D"), loginHint, "WorkMonitor",
                Path.Combine(dataDirectory, "authentication-record.json"),
                AllowInteractiveAuthentication: false)));
        services.AddSingleton<TokenCredential>(provider =>
            provider.GetRequiredService<PersistentInteractiveCredential>());
        services.AddSingleton<DevOpsTokenService>();
        services.AddHttpClient("LocalDevOpsIdentity")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .AddTypedClient((client, provider) => new WebDevOpsIdentityVerifier(
                client, provider.GetRequiredService<ILogger<WebDevOpsIdentityVerifier>>()));
        services.AddSingleton<ILocalDevelopmentIdentity, LocalDevelopmentIdentity>();
        services.AddScoped<IWebDevOpsTokenService, LocalDevelopmentDevOpsTokenService>();

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        }).AddMicrosoftIdentityWebApp(configuration.GetSection("AzureAd"));
        services.AddAuthorization(options => options.DefaultPolicy =
            new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(ModeClaim, ModeValue)
                .Build());
        services.Configure<CookieAuthenticationOptions>(
            CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = "WorkMonitor.LocalDevelopment";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.ExpireTimeSpan = TimeSpan.FromHours(2);
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = async context =>
                {
                    var user = context.Principal;
                    if (user is null || !user.HasClaim(ModeClaim, ModeValue))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(
                            CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }
                    var identity = await context.HttpContext.RequestServices
                        .GetRequiredService<ILocalDevelopmentIdentity>()
                        .GetIdentityAsync(context.HttpContext.RequestAborted);
                    if (identity.OwnerId != WebConversationIdentity.GetOwnerId(user))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(
                            CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        services.Configure<OpenIdConnectOptions>(
            OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.ResponseType = OpenIdConnectResponseType.IdToken;
                options.ResponseMode = OpenIdConnectResponseMode.FormPost;
                options.CallbackPath = "/signin-oidc";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                var originalValidated = options.Events.OnTokenValidated;
                options.Events.OnTokenValidated = async context =>
                {
                    if (originalValidated is not null)
                        await originalValidated(context);
                    if (context.Principal is null)
                        throw new WebAuthenticationException(403, "identity_claims_missing",
                            "Microsoft sign-in did not provide an identity.");
                    var identity = await context.HttpContext.RequestServices
                        .GetRequiredService<ILocalDevelopmentIdentity>()
                        .GetIdentityAsync(context.HttpContext.RequestAborted);
                    if (identity.OwnerId != WebConversationIdentity.GetOwnerId(context.Principal))
                        throw new WebAuthenticationException(403, "local_developer_mismatch",
                            "Sign in with the developer's saved Azure account for this localhost instance.");
                    context.Principal.AddIdentity(new ClaimsIdentity(
                        new[] { new Claim(ModeClaim, ModeValue) }));
                };
                var originalRedirect = options.Events.OnRedirectToIdentityProvider;
                options.Events.OnRedirectToIdentityProvider = async context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                        return;
                    }
                    if (originalRedirect is not null)
                        await originalRedirect(context);
                };
                options.Events.OnRemoteFailure = async context =>
                {
                    context.HandleResponse();
                    var failure = context.Failure as WebAuthenticationException ??
                        context.Failure?.InnerException as WebAuthenticationException;
                    if (failure is not null)
                    {
                        context.Response.StatusCode = failure.StatusCode;
                        await context.Response.WriteAsJsonAsync(
                            new { error = failure.Message, errorCode = failure.ErrorCode },
                            context.HttpContext.RequestAborted);
                        return;
                    }
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Basic Microsoft sign-in failed. This mode still requires the tenant to permit sign-in to this application and the /signin-oidc Web callback.",
                        errorCode = "local_sign_in_failed"
                    }, context.HttpContext.RequestAborted);
                };
            });
    }
}
