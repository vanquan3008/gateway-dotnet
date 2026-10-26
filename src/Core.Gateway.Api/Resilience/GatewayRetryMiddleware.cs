using System.Diagnostics.Metrics;
using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.Errors;
using Core.Gateway.Api.RateLimiting.Local;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Core.Gateway.Api.Resilience;

public static class GatewayRetryMiddleware
{
    private static readonly Meter Meter = new(GatewayForwarderErrorMiddleware.MeterName);
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "gateway.proxy.retries",
        unit: "{request}",
        description: "Number of idempotent requests retried after a backend connection failure, by cluster.");

    /// <summary>
    /// Retries body-less idempotent requests on another destination after a connection failure.
    /// </summary>
    /// <param name="proxy">The proxy pipeline builder.</param>
    /// <returns>The same builder.</returns>
    public static IReverseProxyApplicationBuilder UseGatewayRetries(this IReverseProxyApplicationBuilder proxy)
    {
        var defaultAttempts = proxy.ApplicationServices.GetRequiredService<IOptions<GatewayResilienceOptions>>().Value.RetryAttempts;
        proxy.Use(async (context, next) =>
        {
            var attempts = (int?)context.GetRouteMetadataNumber(GatewayRouteMetadata.RetryAttempts) ?? defaultAttempts;

            var request = context.Request;
            var isIdempotentMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method);
            var hasNoBody = (request.ContentLength is null or 0) && !request.Headers.ContainsKey(HeaderNames.TransferEncoding);
            var isReplayable = isIdempotentMethod && hasNoBody;

            if (attempts == 0 || !isReplayable)
            {
                await next(context);
                return;
            }

            var proxyFeature = context.Features.GetRequiredFeature<IReverseProxyFeature>();
            var candidates = proxyFeature.AvailableDestinations;
            for (var attempt = 0; ; attempt++)
            {
                await next(context);

                if (attempt == attempts ||
                    context.Response.HasStarted ||
                    context.RequestAborted.IsCancellationRequested ||
                    context.Features.Get<IForwarderErrorFeature>()?.Error != ForwarderError.Request)
                {
                    return;
                }

                var remaining = candidates.Where(destination => destination != proxyFeature.ProxiedDestination).ToArray();
                if (remaining.Length > 0)
                {
                    candidates = remaining;
                }

                proxyFeature.AvailableDestinations = candidates;
                context.Features.Set<IForwarderErrorFeature>(null);
                context.Response.Clear();
                Retries.Add(1, new KeyValuePair<string, object?>("cluster", GatewayLocalRateLimiter.GetServiceKey(context)));
            }
        });

        return proxy;
    }
}
