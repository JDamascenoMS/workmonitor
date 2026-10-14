using System.Net;

namespace FT_MONITOR.Middleware;

public sealed class LocalDevelopmentOnlyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true)
            address = address.MapToIPv4();
        if (address is null || !IPAddress.IsLoopback(address) ||
            !IsLoopbackHost(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Development mode only accepts direct localhost requests.",
                errorCode = "local_development_only"
            }, context.RequestAborted);
            return;
        }
        await next(context);
    }

    public static void ValidateStartup(
        IWebHostEnvironment environment, IConfiguration configuration, string urls)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "LocalDevelopment authentication is only allowed in the Development environment.");
        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException(
                "LocalDevelopment requires loopback URLs, not custom Kestrel endpoints.");
        var addresses = urls.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length == 0 || addresses.Any(value =>
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/" ||
            !IsLoopbackHost(uri.Host)))
            throw new InvalidOperationException(
                "LocalDevelopment listeners must use localhost, 127.0.0.1 or ::1 only.");
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host.Trim('[', ']'), out var address) &&
             IPAddress.IsLoopback(address));
}
