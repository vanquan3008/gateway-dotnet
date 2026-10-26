using System.Diagnostics.Metrics;
using Core.Gateway.Api.RateLimiting.Local;
using Microsoft.AspNetCore.Http.Timeouts;
using Yarp.ReverseProxy.Forwarder;

namespace Core.Gateway.Api.Resilience;

// YARP 2.x publishes EventCounters only, so forwarding failures are exported through this meter.
internal sealed class GatewayForwarderErrorMiddleware(RequestDelegate next)
{
    public const string MeterName = "Core.Gateway.Proxy";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>(
        "gateway.proxy.errors",
        unit: "{request}",
        description: "Number of requests that YARP failed to forward, by cluster and error.");

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Features.Get<IForwarderErrorFeature>()?.Error is not { } error || error == ForwarderError.None)
        {
            return;
        }

        var timedOut = error == ForwarderError.RequestCanceled &&
            context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested == true;

        // YARP 2.3 treats the middleware's cancellation as a client cancellation (400).
        if (timedOut && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        }

        Errors.Add(1,
            new KeyValuePair<string, object?>("cluster", GatewayLocalRateLimiter.GetServiceKey(context)),
            new KeyValuePair<string, object?>("error", timedOut ? "RequestTimedOut" : error.ToString()));
    }
}
