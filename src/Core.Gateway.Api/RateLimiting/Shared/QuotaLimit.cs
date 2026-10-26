
using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;

namespace Core.Gateway.Api.RateLimiting.Shared;

public sealed class QuotaLimit
{
    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; }

    [Range(1, 86_400)]
    public int WindowSeconds { get; init; } = 60;

    [Range(0, 100_000)]
    public int QueueLimit { get; init; }

    /// <summary>
    /// Copy of this quota with the permit limit multiplied by ratio (minimum 1) and no queue.
    /// </summary>
    /// <param name="ratio">A finite number greater than zero.</param>
    /// <returns>The scaled quota.</returns>
    /// <exception cref="ArgumentOutOfRangeException">ratio is not finite or not positive.</exception>
    public QuotaLimit Scale(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio));
        }

        return new QuotaLimit
        {
            PermitLimit = Math.Max(1, (int)Math.Floor(PermitLimit * ratio)),
            WindowSeconds = WindowSeconds,
            QueueLimit = 0
        };
    }
    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);

    /// <summary>
    /// Converts this quota to a per-pod sliding window limiter.
    /// </summary>
    /// <returns>The limiter options.</returns>
    public SlidingWindowRateLimiterOptions ToSlidingWindow() => new()
    {
        AutoReplenishment = true,
        PermitLimit = PermitLimit,
        QueueLimit = 0,
        Window = Window,
        SegmentsPerWindow = 10
    };
}
