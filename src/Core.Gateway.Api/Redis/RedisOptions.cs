using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.Redis;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";
    public string? ConnectionString { get; init; }

    [Required, StringLength(128), RegularExpression(@"^[a-zA-Z0-9:_-]+$")]
    public string KeyPrefix { get; init; } = "gateway";

    [Range(50, 5_000)]
    public int TimeoutMilliseconds { get; init; } = 250;

    [Range(100, 30_000)]
    public int ConnectTimeoutMilliseconds { get; init; } = 2_000;

    [Range(1, 300)]
    public int FailureCooldownSeconds { get; init; } = 5;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
