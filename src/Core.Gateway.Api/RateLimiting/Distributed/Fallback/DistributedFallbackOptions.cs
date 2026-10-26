using System.ComponentModel.DataAnnotations;
namespace Core.Gateway.Api.RateLimiting.Distributed.Fallback;

public sealed class DistributedFallbackOptions
{
    public bool UseLocalFallbackWhenStoreUnavailable { get; init; } = true;

    /// <summary>
    /// Fixed share of every quota a pod enforces while Redis is down. With <see cref="AutoScaleRatio"/> it only
    /// applies until the pod has counted its peers.
    /// </summary>
    [Range(0.01, 1.0)]
    public double LocalRatio { get; init; } = 0.5;

    /// <summary>
    /// Use 1 / (live pods) instead of <see cref="LocalRatio"/>, so the whole cluster stays near the shared quota
    /// however far the HPA has scaled.
    /// </summary>
    public bool AutoScaleRatio { get; init; } = true;

    [Range(1, 60)]
    public int HeartbeatSeconds { get; init; } = 30;
}
