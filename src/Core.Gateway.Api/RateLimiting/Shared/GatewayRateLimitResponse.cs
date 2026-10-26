using System.Globalization;
using System.Threading.RateLimiting;
using Core.Gateway.Api.Headers;
using Microsoft.AspNetCore.RateLimiting;

namespace Core.Gateway.Api.RateLimiting.Shared;

internal static class GatewayRateLimitResponse
{

    /// <summary>
    /// Writes the 429 for a request rejected by the ASP.NET Core limiter.
    /// </summary>
    /// <param name="context">The rejection context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when written.</returns>
    public static ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        TimeSpan? retryAfter = null;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfterValue))
        {
            retryAfter = retryAfterValue;
        }

        Task responseTask = WriteQuotaExceededAsync(context.HttpContext, retryAfter);
        return new ValueTask(responseTask);
    }

    /// <summary>
    /// Writes the 429 problem response, with Retry-After when the wait is known.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="retryAfter">How long the client should wait.</param>
    /// <returns>A task that completes when the response was written.</returns>
    public static Task WriteQuotaExceededAsync(HttpContext context, TimeSpan? retryAfter = null)
    {
        return WriteProblemAsync(
            context,
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too Many Requests",
            detail: "The request rate limit was exceeded.",
            retryAfter: retryAfter
        );
    }

    /// <summary>
    /// Writes the 503 problem response used when Redis is down and no fallback is allowed.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="retryAfter">Time until Redis may be tried again.</param>
    /// <returns>A task that completes when the response was written.</returns>
    public static Task WriteStoreUnavailableAsync(HttpContext context, TimeSpan retryAfter = default)
    {
        TimeSpan? effectiveRetryAfter = retryAfter > TimeSpan.Zero ? retryAfter : null;

        return WriteProblemAsync(
            context,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "The shared rate limit store is unavailable.",
            retryAfter: effectiveRetryAfter
        );
    }

    /// <summary>
    /// Writes a problem response with the correlation id and an optional Retry-After.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="statusCode">HTTP status.</param>
    /// <param name="title">Problem title.</param>
    /// <param name="detail">Problem detail.</param>
    /// <param name="retryAfter">Wait time, if known.</param>
    /// <returns>A task that completes when written.</returns>
    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail,
        TimeSpan? retryAfter = null)
    {
        if (retryAfter.HasValue)
        {
            int retryAfterSeconds = (int)Math.Ceiling(retryAfter.Value.TotalSeconds);
            int formattedSeconds = Math.Max(1, retryAfterSeconds);
            context.Response.Headers.RetryAfter = formattedSeconds.ToString(CultureInfo.InvariantCulture);
        }
        string correlationId = context.Request.Headers[GatewayHeaders.CorrelationId].ToString();
        var extensions = new Dictionary<string, object?>
        {
            ["correlationId"] = correlationId
        };

        IResult problemResult = Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: extensions
        );

        await problemResult.ExecuteAsync(context);
    }
}
