using System.Diagnostics;
using Core.Gateway.Api.Headers;

namespace Core.Gateway.Api.Observability;

/// <summary>
/// Logs each request; slow, aborted and 5xx requests log at a higher level.
/// </summary>
public sealed partial class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    private static readonly TimeSpan SlowRequest = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Logs request start and completion with timing, status and ids.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request was handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var method = context.Request.Method;
        var path = context.Request.PathBase.Add(context.Request.Path).Value ?? "/";
        var correlationId = context.Request.Headers[GatewayHeaders.CorrelationId].ToString();
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        RequestStarted(method, path, correlationId, traceId);

        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var aborted = context.RequestAborted.IsCancellationRequested;
            var statusCode = context.Response.StatusCode;
            var level = statusCode >= 500 || aborted ? LogLevel.Warning
                : elapsed >= SlowRequest ? LogLevel.Information
                : LogLevel.Debug;

            RequestCompleted(level, method, path, statusCode, aborted, elapsed.TotalMilliseconds, correlationId, traceId);
        }
    }

    /// <summary>
    /// Log message: request started.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Request path.</param>
    /// <param name="correlationId">Correlation id.</param>
    /// <param name="traceId">Trace id.</param>
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Request started {Method} {Path} CorrelationId={CorrelationId} TraceId={TraceId}")]
    private partial void RequestStarted(string method, string path, string correlationId, string traceId);

    /// <summary>
    /// Log message: request completed.
    /// </summary>
    /// <param name="level">Log level.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Request path.</param>
    /// <param name="statusCode">Response status.</param>
    /// <param name="aborted">Whether the client aborted.</param>
    /// <param name="elapsedMilliseconds">Duration.</param>
    /// <param name="correlationId">Correlation id.</param>
    /// <param name="traceId">Trace id.</param>
    [LoggerMessage(EventId = 2,
        Message = "Request completed {Method} {Path} StatusCode={StatusCode} Aborted={Aborted} ElapsedMilliseconds={ElapsedMilliseconds} CorrelationId={CorrelationId} TraceId={TraceId}")]
    private partial void RequestCompleted(LogLevel level, string method, string path, int statusCode, bool aborted,
        double elapsedMilliseconds, string correlationId, string traceId);
}
