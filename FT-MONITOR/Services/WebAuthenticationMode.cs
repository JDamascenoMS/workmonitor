namespace FT_MONITOR.Services;

public enum WebAuthenticationMode
{
    SpaPkce,
    LocalDevelopment
}

public static class WebAuthenticationModeConfiguration
{
    public static WebAuthenticationMode GetMode(IConfiguration configuration)
    {
        var value = configuration["WorkMonitor:Web:AuthenticationMode"];
        return value switch
        {
            null or "" => WebAuthenticationMode.SpaPkce,
            _ when string.Equals(value, "SpaPkce", StringComparison.OrdinalIgnoreCase)
                => WebAuthenticationMode.SpaPkce,
            _ when string.Equals(value, "LocalDevelopment", StringComparison.OrdinalIgnoreCase)
                => WebAuthenticationMode.LocalDevelopment,
            _ => throw new InvalidOperationException(
                "WorkMonitor:Web:AuthenticationMode must be SpaPkce or LocalDevelopment.")
        };
    }
}
