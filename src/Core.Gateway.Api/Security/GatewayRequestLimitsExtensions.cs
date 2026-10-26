using Core.Gateway.Api.Configuration;
using Microsoft.AspNetCore.Http.Features;

namespace Core.Gateway.Api.Security;

public static class GatewayRequestLimitsExtensions
{
    /// <summary>
    /// Enforces the per-route max request body size, answering 413 when it is exceeded.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static IApplicationBuilder UseGatewayRequestLimits(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.GetRouteMetadataNumber(GatewayRouteMetadata.MaxRequestBodySize) is not { } limit)
            {
                return next(context);
            }
            if (context.Request.ContentLength > limit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return Task.CompletedTask;
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            {
                bodySize.MaxRequestBodySize = limit;
            }

            return next(context);
        });
}
