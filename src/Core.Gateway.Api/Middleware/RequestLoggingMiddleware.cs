using System.Diagnostics;
using Core.Gateway.Api.Transformations;

namespace Core.Gateway.Api.Middleware;

public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var method = context.Request.Method;
        var path = context.Request.PathBase.Add(context.Request.Path).Value ?? "/";
        var correlationId = context.Request.Headers[GatewayHeaders.CorrelationId].ToString();
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        logger.LogDebug(
            "Request started {Method} {Path} CorrelationId={CorrelationId} TraceId={TraceId}",
            method,
            path,
            correlationId,
            traceId);

        try
        {
            await next(context);
        }
        finally
        {
            logger.LogInformation(
                "Request completed {Method} {Path} StatusCode={StatusCode} Aborted={Aborted} ElapsedMilliseconds={ElapsedMilliseconds} CorrelationId={CorrelationId} TraceId={TraceId}",
                method,
                path,
                context.Response.StatusCode,
                context.RequestAborted.IsCancellationRequested,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                correlationId,
                traceId);
        }
    }
}
