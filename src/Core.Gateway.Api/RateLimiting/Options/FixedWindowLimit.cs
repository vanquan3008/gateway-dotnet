using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.RateLimiting.Options;

public sealed class FixedWindowLimit
{
    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; }

    [Range(1, 86_400)]
    public int WindowSeconds { get; init; } = 60;

    [Range(0, 100_000)]
    public int QueueLimit { get; init; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
