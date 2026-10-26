using Core.Gateway.Api.RateLimiting.Shared;

namespace Core.Gateway.Api.RateLimiting.Distributed.Fallback;

/// <summary>
/// Last known number of live gateway pods, published by the Redis heartbeat. While Redis is down it keeps
/// the last value, so the local fallback can still split each quota across the pods that shared it.
/// </summary>
internal sealed class ActiveInstanceCounter
{
    private int _count;

    /// <summary>
    /// Creates the counter and publishes it as a metric.
    /// </summary>
    public ActiveInstanceCounter() => GatewayRateLimitingMetrics.ObserveActiveInstances(() => Count);

    public int? Count => Volatile.Read(ref _count) is var count and > 0 ? count : null;

    /// <summary>
    /// Stores the live pod count (at least 1).
    /// </summary>
    /// <param name="count">Pods counted in Redis.</param>
    public void Set(int count) => Volatile.Write(ref _count, Math.Max(1, count));
}
