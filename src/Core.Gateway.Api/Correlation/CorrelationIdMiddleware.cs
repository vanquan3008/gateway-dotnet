using System.Diagnostics;
using Core.Gateway.Api.Headers;
using Microsoft.Extensions.Primitives;

namespace Core.Gateway.Api.Correlation;

/// <summary>
/// Keeps a valid caller correlation id or creates one, and carries it to the response, logs and traces.
/// </summary>
public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    private const int MaxCorrelationIdLength = 64;

    /// <summary>
    /// Sets the correlation id on the request, the response header, the trace tag and the log scope.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request was handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[GatewayHeaders.CorrelationId];
        var supplied = values.Count == 1 ? values[0] : null;
        var isValid = !string.IsNullOrWhiteSpace(supplied)
            && supplied.Length <= MaxCorrelationIdLength
            && supplied.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
        var correlationId = isValid ? supplied! : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        context.Request.Headers[GatewayHeaders.CorrelationId] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[GatewayHeaders.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId
        });

        await next(context);
    }
}
