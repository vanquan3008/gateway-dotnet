using System.Diagnostics;
using Core.Gateway.Api.Transformations;
using Microsoft.Extensions.Primitives;

namespace Core.Gateway.Api.Middleware;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    private const int MaxCorrelationIdLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[GatewayHeaders.CorrelationId];
        var correlationId = values.Count == 1 && IsValidCorrelationId(values[0])
                ? values[0]! : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

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

    private static bool IsValidCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxCorrelationIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }
}
