using System.Globalization;
using System.Threading.RateLimiting;
using Core.Gateway.Api.Transformations;
using Microsoft.AspNetCore.RateLimiting;

namespace Core.Gateway.Api.RateLimiting.Shared;

internal static class GatewayRateLimitResponse
{
    public static ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : null;
        return new ValueTask(WriteQuotaExceededAsync(context.HttpContext, retryAfter));
    }

    public static Task WriteQuotaExceededAsync(HttpContext context, TimeSpan? retryAfter = null) =>
        WriteProblemAsync(context, StatusCodes.Status429TooManyRequests,
            "Too Many Requests", "The request rate limit was exceeded.", retryAfter);

    public static Task WriteStoreUnavailableAsync(HttpContext context) =>
        WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable,
            "Service Unavailable", "The shared rate limit store is unavailable.");

    private static async Task WriteProblemAsync(HttpContext context, int statusCode,
        string title, string detail, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } retry)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        }

        await Results.Problem(
                statusCode: statusCode,
                title: title,
                detail: detail,
                extensions: new Dictionary<string, object?>
                {
                    ["correlationId"] = context.Request.Headers[GatewayHeaders.CorrelationId].ToString()
                })
            .ExecuteAsync(context);
    }
}
