using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Core.Gateway.Api.HealthChecks;

public static class GatewayHealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Writes the health report as JSON.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="report">The health report.</param>
    /// <returns>A task that completes when the body was written.</returns>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                status = report.Status.ToString(),
                durationMilliseconds = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    durationMilliseconds = entry.Value.Duration.TotalMilliseconds,
                    description = entry.Value.Description
                })
            },
            JsonOptions,
            context.RequestAborted);
    }
}
