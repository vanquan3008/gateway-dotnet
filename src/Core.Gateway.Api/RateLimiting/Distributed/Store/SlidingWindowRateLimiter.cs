
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Core.Gateway.Api.RateLimiting.Shared;

namespace Core.Gateway.Api.RateLimiting.Distributed.Store;

/// <summary>
/// Sliding window counter on top of a counter storage.
/// </summary>
internal sealed class SlidingWindowRateLimiter(
    IRateLimitCounterStorage storage,
    TimeProvider time)
{
    /// <summary>
    /// Sliding window counter: allowed while current + weighted previous window stays within the limit; rejected requests are refunded.
    /// </summary>
    /// <param name="partitionKey">Identifies the quota.</param>
    /// <param name="permitLimit">Requests allowed per window.</param>
    /// <param name="window">Window length in whole milliseconds.</param>
    /// <param name="cancellationToken">Cancels the Redis call.</param>
    /// <returns>Allowed or Rejected, with Retry-After.</returns>
    /// <exception cref="ArgumentException">The partition key is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit or window is invalid.</exception>
    /// <exception cref="Core.Gateway.Api.Redis.RedisUnavailableException">Redis could not be reached.</exception>
    public async ValueTask<DistributedRateLimitResult> AcquireAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            window,
            TimeSpan.Zero);

        if (window.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window),
                window,
                "The window must use whole milliseconds.");
        }

        var windowMilliseconds = checked((long)window.TotalMilliseconds);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            windowMilliseconds);

        cancellationToken.ThrowIfCancellationRequested();

        var nowMilliseconds = time.GetUtcNow().ToUnixTimeMilliseconds();

        var windowIndex = nowMilliseconds / windowMilliseconds;

        var elapsedMilliseconds =
            nowMilliseconds - (windowIndex * windowMilliseconds);

        var remainingMilliseconds =
            windowMilliseconds - elapsedMilliseconds;

        var counter = await storage.IncrementAsync(
            partitionKey,
            windowMilliseconds,
            windowIndex,
            cancellationToken);

        var previousWindowWeight =
            remainingMilliseconds / (double)windowMilliseconds;

        var estimatedRequestCount =
            counter.Current
            + (counter.Previous * previousWindowWeight);

        if (estimatedRequestCount <= permitLimit)
        {
            return DistributedRateLimitResult.Allowed(
                ToPositiveMilliseconds(remainingMilliseconds),
                permitLimit,
                (int)Math.Max(0d, Math.Floor(permitLimit - estimatedRequestCount)));
        }

        if (!await storage.DecrementAsync(
                partitionKey,
                windowMilliseconds,
                windowIndex))
        {
            GatewayRateLimitingMetrics.RecordRefundFailed();
        }

        var retryAfter = CalculateRetryAfter(
            currentCount: counter.Current - 1,
            previousCount: counter.Previous,
            permitLimit: permitLimit,
            windowMilliseconds: windowMilliseconds,
            elapsedMilliseconds: elapsedMilliseconds);

        return DistributedRateLimitResult.Rejected(retryAfter, permitLimit);
    }

    /// <summary>
    /// How long a rejected client must wait before one more request fits.
    /// </summary>
    /// <param name="currentCount">Requests in the current window.</param>
    /// <param name="previousCount">Requests in the previous window.</param>
    /// <param name="permitLimit">Requests allowed per window.</param>
    /// <param name="windowMilliseconds">Window length.</param>
    /// <param name="elapsedMilliseconds">Time already elapsed in the current window.</param>
    /// <returns>At least one millisecond.</returns>
    /// <exception cref="InvalidOperationException">The computed duration is not finite.</exception>
    private static TimeSpan CalculateRetryAfter(
        long currentCount,
        long previousCount,
        int permitLimit,
        long windowMilliseconds,
        long elapsedMilliseconds)
    {
        var remainingMilliseconds =
            windowMilliseconds - elapsedMilliseconds;

        double retryAfterMilliseconds = remainingMilliseconds;

        if (currentCount < permitLimit)
        {
            if (previousCount > 0)
            {
                var remainingCapacity =
                    permitLimit - currentCount - 1;

                var requiredElapsedFraction =
                    1d - (remainingCapacity / (double)previousCount);

                retryAfterMilliseconds =
                    (windowMilliseconds * requiredElapsedFraction)
                    - elapsedMilliseconds;
            }
        }
        else
        {
            var requiredPreviousWindowFraction =
                1d - ((permitLimit - 1d) / currentCount);

            retryAfterMilliseconds =
                remainingMilliseconds
                + (windowMilliseconds * requiredPreviousWindowFraction);
        }

        return ToPositiveMilliseconds(retryAfterMilliseconds);
    }

    /// <summary>
    /// Rounds a duration up to a whole number of milliseconds, at least one.
    /// </summary>
    /// <param name="milliseconds">The duration.</param>
    /// <returns>The duration.</returns>
    /// <exception cref="InvalidOperationException">The value is not finite.</exception>
    private static TimeSpan ToPositiveMilliseconds(double milliseconds)
    {
        if (double.IsNaN(milliseconds)
            || double.IsInfinity(milliseconds))
        {
            throw new InvalidOperationException(
                "The calculated rate limit duration is not finite.");
        }

        var roundedMilliseconds = Math.Max(
            1d,
            Math.Ceiling(milliseconds));

        return TimeSpan.FromMilliseconds(roundedMilliseconds);
    }
}
