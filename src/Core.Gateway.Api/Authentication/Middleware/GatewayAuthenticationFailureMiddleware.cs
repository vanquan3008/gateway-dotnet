using Core.Gateway.Api.Authentication.Metrics;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Core.Gateway.Api.Authentication.Middleware;

/// <summary>
/// Answers 503 instead of a misleading 401 when the identity provider cannot be reached.
/// </summary>
internal sealed class GatewayAuthenticationFailureMiddleware(
    RequestDelegate next)
{
    private static readonly object MetadataUnavailableKey = new();

    /// <summary>
    /// Returns 503 when this request was flagged as having no identity provider metadata; otherwise continues.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request was handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Items.ContainsKey(MetadataUnavailableKey))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service Unavailable",
                detail: "Authentication metadata is temporarily unavailable.")
                .ExecuteAsync(context);

            return;
        }

        await next(context);
    }

    /// <summary>
    /// Flags the request as failed because identity provider metadata is unavailable, counts it and fails the authentication.
    /// </summary>
    /// <param name="context">The JWT message context.</param>
    /// <param name="exception">Why the metadata could not be loaded.</param>
    internal static void MarkMetadataUnavailable(
        MessageReceivedContext context,
        Exception exception)
    {
        context.HttpContext.Items[MetadataUnavailableKey] = true;
        GatewayAuthenticationMetrics.Failed(GatewayAuthenticationMetrics.Jwt, GatewayAuthenticationMetrics.Reasons.IdpUnavailable);
        context.Fail(exception);
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<GatewayAuthenticationFailureMiddleware>>();
        logger.LogWarning("Authentication metadata is unavailable. Request rejected. Exception: {ExceptionType}", exception.GetType().Name);
    }
}
