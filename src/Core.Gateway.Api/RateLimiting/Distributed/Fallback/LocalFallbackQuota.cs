using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting.Distributed.Fallback;

internal sealed class LocalFallbackQuota : IDisposable
{
    private readonly DistributedFallbackOptions _options;
    private readonly ActiveInstanceCounter _instances;

    private readonly PartitionedRateLimiter<GatewayRateLimitPartition> _limiter =
        PartitionedRateLimiter.Create<GatewayRateLimitPartition, string>(
            partition =>
            {
                var limiterKey = $"{partition.Key}|{partition.Limit.PermitLimit}";

                return RateLimitPartition.GetSlidingWindowLimiter(
                    limiterKey,
                    _ => partition.Limit.ToSlidingWindow());
            });

    /// <summary>
    /// Creates the fallback quota.
    /// </summary>
    /// <param name="options">The rate limiting options.</param>
    /// <param name="instances">The live pod count.</param>
    public LocalFallbackQuota(
        IOptions<GatewayRateLimitingOptions> options,
        ActiveInstanceCounter instances)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(instances);

        _options = options.Value.Distributed.Fallback;
        _instances = instances;
    }

    /// <summary>
    /// Decides a quota inside this pod when Redis cannot: the full quota with no store, this pod's share when Redis is down, or Unavailable if the fallback is off.
    /// </summary>
    /// <param name="partition">The quota the request would have consumed in Redis.</param>
    /// <param name="storeConfigured">Whether a shared store is configured.</param>
    /// <param name="unavailableRetryAfter">Retry-After to report when the fallback is off.</param>
    /// <returns>Allowed, Rejected or Unavailable.</returns>
    public DistributedRateLimitResult Resolve(
        GatewayRateLimitPartition partition,
        bool storeConfigured,
        TimeSpan unavailableRetryAfter)
    {
        if (!storeConfigured)
        {
            return TryAcquire(partition);
        }
        if (!_options.UseLocalFallbackWhenStoreUnavailable)
        {
            return DistributedRateLimitResult.Unavailable(unavailableRetryAfter);
        }
        var fallbackPartition = partition with
        {
            Limit = partition.Limit.Scale(GetLocalRatio())
        };

        return TryAcquire(fallbackPartition);
    }

    /// <summary>
    /// This pod's share of a quota: 1 / live pods, or the fixed LocalRatio until pods are counted.
    /// </summary>
    /// <returns>A ratio in (0, 1].</returns>
    private double GetLocalRatio()
    {
        if (!_options.AutoScaleRatio)
        {
            return _options.LocalRatio;
        }

        var instanceCount = _instances.Count;

        return instanceCount is > 0
            ? 1d / instanceCount.Value
            : _options.LocalRatio;
    }

    /// <summary>
    /// Takes one permit from the in-memory sliding window of the partition.
    /// </summary>
    /// <param name="partition">The quota to consume.</param>
    /// <returns>Allowed, or Rejected with Retry-After.</returns>
    private DistributedRateLimitResult TryAcquire(
        GatewayRateLimitPartition partition)
    {
        using var lease = _limiter.AttemptAcquire(partition);

        if (lease.IsAcquired)
        {
            return DistributedRateLimitResult.Allowed(TimeSpan.Zero);
        }

        var retryAfter = lease.TryGetMetadata(
            MetadataName.RetryAfter,
            out var metadata)
            ? metadata
            : partition.Limit.Window;

        return DistributedRateLimitResult.Rejected(retryAfter);
    }

    /// <summary>
    /// Releases the limiter.
    /// </summary>
    public void Dispose()
    {
        _limiter.Dispose();
    }
}
