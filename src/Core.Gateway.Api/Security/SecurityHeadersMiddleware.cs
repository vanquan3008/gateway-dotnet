namespace Core.Gateway.Api.Security;

/// <summary>
/// Adds basic security headers to every response.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate _next)
{
    /// <summary>
    /// Adds X-Content-Type-Options, X-Frame-Options and Referrer-Policy when the response starts.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request was handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.TryAdd(
                "X-Content-Type-Options",
                "nosniff");
            context.Response.Headers.TryAdd(
                "X-Frame-Options",
                "DENY");
            context.Response.Headers.TryAdd(
                "Referrer-Policy",
                "no-referrer");
            context.Response.Headers.TryAdd(
                "X-XSS-Protection",
                "0");
            context.Response.Headers.TryAdd(
                "Cross-Origin-Opener-Policy",
                "same-origin");
            context.Response.Headers.TryAdd(
                "Permissions-Policy",
                "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");

            return Task.CompletedTask;
        });
        await _next(context);
    }
}
