namespace Core.Gateway.Api.Middleware;

public sealed class SecurityHeadersMiddleware(RequestDelegate _next)
{
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

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
