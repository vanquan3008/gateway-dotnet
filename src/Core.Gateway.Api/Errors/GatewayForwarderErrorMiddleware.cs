using System.Diagnostics.Metrics;
using Core.Gateway.Api.RateLimiting.Local;
using Microsoft.AspNetCore.Http.Timeouts;
using Yarp.ReverseProxy.Forwarder;

namespace Core.Gateway.Api.Errors;

/// <summary>
/// Counts proxy forwarding failures and answers 504 when the request timed out.
/// </summary>
internal sealed class GatewayForwarderErrorMiddleware(RequestDelegate next)
{
    public const string MeterName = "Core.Gateway.Proxy";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>(
        "gateway.proxy.errors",
        unit: "{request}",
        description: "Number of requests that YARP failed to forward, by cluster and error.");

    /// <summary>
    /// Runs the rest of the pipeline, then records a forwarding error and sets 504 for a timed-out request.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request was handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Features.Get<IForwarderErrorFeature>()?.Error is not (not null and not ForwarderError.None and var error))
        {
            return;
        }

        var timedOut = error == ForwarderError.RequestCanceled &&
            context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested == true;

        if (timedOut && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        }

        if (Errors.Enabled)
        {
            Errors.Add(1,
                new KeyValuePair<string, object?>("cluster", GatewayLocalRateLimiter.GetServiceKey(context)),
                new KeyValuePair<string, object?>("error", timedOut ? "RequestTimedOut" : error.ToString()));
        }
    }
}
