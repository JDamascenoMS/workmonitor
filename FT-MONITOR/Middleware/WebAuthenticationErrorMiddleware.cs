using FT_MONITOR.Services;

namespace FT_MONITOR.Middleware;

public sealed class WebAuthenticationErrorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (WebAuthenticationException ex) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(
                new { error = ex.Message, errorCode = ex.ErrorCode },
                context.RequestAborted);
        }
    }
}
