namespace Core.Gateway.Api.RateLimiting.Distributed;

public readonly record struct DistributedRateLimitResult(DistributedRateLimitStatus Status, TimeSpan RetryAfter)
{
    public static DistributedRateLimitResult Unavailable => new DistributedRateLimitResult(DistributedRateLimitStatus.Unavailable, TimeSpan.Zero);

    public static DistributedRateLimitResult Allowed(TimeSpan retryAfter) =>
        new DistributedRateLimitResult(DistributedRateLimitStatus.Allowed, retryAfter);

    public static DistributedRateLimitResult Rejected(TimeSpan retryAfter) =>
        new DistributedRateLimitResult(DistributedRateLimitStatus.Rejected, retryAfter);
}
