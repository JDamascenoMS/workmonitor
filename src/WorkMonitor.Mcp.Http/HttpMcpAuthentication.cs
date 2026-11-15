using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace WorkMonitor.Mcp.Http;

public static class HttpMcpAuthentication
{
    public const string Policy = "HttpMcpUser";

    public static void AddHttpMcpAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<HttpMcpSettings>((options, settings) =>
        {
            options.Authority = settings.Authority;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.SaveToken = true;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidateIssuer = true,
                ValidIssuer = settings.Authority,
                ValidateAudience = true,
                ValidAudience = settings.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            };
        });
        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.Resource is HttpContext http &&
                    IsDelegatedUser(context.User, http.RequestServices.GetRequiredService<HttpMcpSettings>()))
                .Build();
            options.AddPolicy(Policy, policy);
            options.FallbackPolicy = policy;
        });
    }

    public static bool IsDelegatedUser(ClaimsPrincipal user, HttpMcpSettings settings)
        => user.Identity?.IsAuthenticated == true &&
            SingleClaim(user, "ver") == "2.0" &&
            SingleClaim(user, "tid") == settings.TenantId &&
            Guid.TryParse(SingleClaim(user, "oid"), out var userId) && userId != Guid.Empty &&
            !user.HasClaim("idtyp", "app") &&
            SingleClaim(user, "scp")?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(settings.RequiredScope, StringComparer.Ordinal) == true;

    private static string? SingleClaim(ClaimsPrincipal user, string type)
    {
        var values = user.FindAll(type).ToArray();
        return values.Length == 1 ? values[0].Value : null;
    }
}
