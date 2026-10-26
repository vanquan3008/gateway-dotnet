using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Options;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting.Local;

internal sealed class GatewayConcurrencyLimiterMiddleware : IMiddleware, IDisposable
{
    public const string MeterName = "Core.Gateway.Concurrency";
    private static readonly Meter Meter = new(MeterName);
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("gateway.concurrency.active");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("gateway.concurrency.rejected");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gateway.concurrency.duration", "s");
    private readonly PartitionedRateLimiter<HttpContext>? _limiter;

    public GatewayConcurrencyLimiterMiddleware(IOptions<GatewayRateLimitingOptions> options)
    {
        if (options.Value.Enabled && options.Value.Local.Enabled)
        {
            _limiter = GatewayLocalRateLimiter.CreateConcurrencyLimiter(options.Value.Local);
        }
    }

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
        var tag = new KeyValuePair<string, object?>("cluster", GatewayLocalRateLimiter.GetServiceKey(context));
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

    public void Dispose() => _limiter?.Dispose();
}
