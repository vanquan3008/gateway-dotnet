using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.RateLimiting.Options;

public sealed class LocalRateLimitingOptions
{
    public bool Enabled { get; init; } = true;

    // Per-pod circuit breakers: keep them above the throughput a pod sustains at the HPA CPU target,
    // otherwise pods reject traffic while CPU stays low and the autoscaler never adds replicas.
    public FixedWindowLimit Instance { get; init; } = new() { PermitLimit = 5_000, WindowSeconds = 1 };

    public FixedWindowLimit Service { get; init; } = new() { PermitLimit = 2_000, WindowSeconds = 1 };

    // Optional IP protection for anonymous traffic, independent of the shared IP quota.
    public FixedWindowLimit? Anonymous { get; init; }

    [Range(1, 100_000)]
    public int InstanceConcurrencyLimit { get; init; } = 1_024;

    [Range(1, 100_000)]
    public int ServiceConcurrencyLimit { get; init; } = 256;
}
