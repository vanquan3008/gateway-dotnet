using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using static Core.Gateway.Api.RateLimiting.Shared.GatewayRateLimitingNames;

namespace Core.Gateway.Api.RateLimiting.Local;

internal sealed class GatewayConcurrencyLimiterMiddleware : IMiddleware, IDisposable
{
    private static readonly Meter Meter = new(GatewayConcurrencyMetricNames.MeterName);
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>(GatewayConcurrencyMetricNames.ActiveMetricName);
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>(GatewayConcurrencyMetricNames.RejectedMetricName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        GatewayConcurrencyMetricNames.DurationMetricName,
        GatewayConcurrencyMetricNames.DurationMetricUnit);
    private readonly PartitionedRateLimiter<HttpContext>? _limiter;

    /// <summary>
    /// Builds the concurrency limiter when local limiting is enabled.
    /// </summary>
    /// <param name="options">The rate limiting options.</param>
    public GatewayConcurrencyLimiterMiddleware(
        IOptions<GatewayRateLimitingOptions> options)
    {
        if (options.Value.Enabled && options.Value.Local.Enabled)
        {
            _limiter = GatewayLocalRateLimiter.CreateConcurrencyLimiter(options.Value.Local);
        }
    }

    /// <summary>
    /// Limits requests running at once per backend cluster and per pod; excess requests get 429 immediately.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task that completes when the request finished or was rejected.</returns>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (_limiter is null ||
            context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
        {
            await next(context);
            return;
        }

        context.RequestAborted.ThrowIfCancellationRequested();
        using var lease = _limiter.AttemptAcquire(context);
        var tag = new KeyValuePair<string, object?>(MetricTag.Cluster, GatewayLocalRateLimiter.GetServiceKey(context));
        if (!lease.IsAcquired)
        {
            Rejected.Add(1, tag);
            await GatewayRateLimitResponse.WriteQuotaExceededAsync(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        Active.Add(1, tag);
        try
        {
            await next(context);
        }
        finally
        {
            Active.Add(-1, tag);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tag);
        }
    }

    /// <summary>
    /// Releases the limiter.
    /// </summary>
    public void Dispose() => _limiter?.Dispose();
}
