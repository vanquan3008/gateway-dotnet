namespace Core.Gateway.Api.RateLimiting.Distributed.Store;

/// <summary>
/// Answer of a shared quota check. Limit and Remaining are set only when a shared store answered.
/// </summary>
/// <param name="Status">Allowed, Rejected or Unavailable.</param>
/// <param name="RetryAfter">Rejected: wait before retrying. Allowed: time left in the current window.</param>
public readonly record struct DistributedRateLimitResult(DistributedRateLimitStatus Status, TimeSpan RetryAfter)
{
    public int? Limit { get; init; }

    public int? Remaining { get; init; }

    /// <summary>
    /// The store could not answer.
    /// </summary>
    /// <param name="retryAfter">Time until the store may be tried again.</param>
    /// <returns>The result.</returns>
    public static DistributedRateLimitResult Unavailable(TimeSpan retryAfter = default) =>
        new DistributedRateLimitResult(DistributedRateLimitStatus.Unavailable, retryAfter);

    /// <summary>
    /// The request is within the quota.
    /// </summary>
    /// <param name="retryAfter">Time left in the current window.</param>
    /// <param name="limit">The quota limit.</param>
    /// <param name="remaining">Permits left.</param>
    /// <returns>The result.</returns>
    public static DistributedRateLimitResult Allowed(TimeSpan retryAfter, int? limit = null, int? remaining = null) =>
        new DistributedRateLimitResult(DistributedRateLimitStatus.Allowed, retryAfter)
        {
            Limit = limit,
            Remaining = remaining
        };

    /// <summary>
    /// The quota is used up.
    /// </summary>
    /// <param name="retryAfter">How long to wait.</param>
    /// <param name="limit">The quota limit.</param>
    /// <returns>The result.</returns>
    public static DistributedRateLimitResult Rejected(TimeSpan retryAfter, int? limit = null) =>
        new DistributedRateLimitResult(DistributedRateLimitStatus.Rejected, retryAfter)
        {
            Limit = limit,
            Remaining = limit is null ? null : 0
        };
}
