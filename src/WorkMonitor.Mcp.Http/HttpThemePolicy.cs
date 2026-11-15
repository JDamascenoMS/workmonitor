using System.Globalization;
using System.Threading.RateLimiting;

namespace WorkMonitor.Mcp.Http;

public sealed class HttpThemePolicy : IDisposable
{
    public TimeSpan Deadline { get; }
    private readonly PartitionedRateLimiter<string> _limiter;

    public HttpThemePolicy(IConfiguration configuration)
    {
        var section = configuration.GetSection("WorkMonitor:HttpMcp:Themes");
        int Setting(string name, int fallback, int maximum)
        {
            var raw = section[name];
            var value = raw == null ? fallback : int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            return value >= 1 && value <= maximum ? value : throw new InvalidOperationException(
                $"WorkMonitor:HttpMcp:Themes:{name} must be an integer between 1 and {maximum}.");
        }
        Deadline = TimeSpan.FromSeconds(Setting("TimeoutSeconds", 60, 120));
        var permits = Setting("RequestsPerWindow", 2, 10);
        var window = TimeSpan.FromSeconds(Setting("WindowSeconds", 60, 300));
        var concurrency = Setting("ConcurrentPerUser", 1, 2);
        _limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetConcurrencyLimiter(key, _ =>
                new ConcurrencyLimiterOptions { PermitLimit = concurrency, QueueLimit = 0 })),
            PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true
                })));
    }

    public RateLimitLease Acquire(string tenant, string user) => _limiter.AttemptAcquire(tenant + ":" + user);
    public void Dispose() => _limiter.Dispose();
}

public sealed class RequestThemeAnalysis
{
    internal Func<Task<bool>>? Preflight { get; set; }
    internal object? Result { get; set; }
    public object GetResult() => Result ?? throw new InvalidOperationException("Request-bound theme preflight is required.");

    internal async Task PrepareAsync()
    {
        var preflight = Preflight;
        Preflight = null;
        if (preflight != null && !await preflight())
            throw new PreflightRejectedException();
    }

    internal sealed class PreflightRejectedException : Exception;
}
