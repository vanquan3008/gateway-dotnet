using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.RateLimiting.Options;

public sealed class DistributedRateLimitingOptions
{
    public bool Enabled { get; init; }
    public string? RedisConnectionString { get; init; }

    [Required]
    public string UserIdClaim { get; init; } = "sub";

    [Required]
    public string TenantIdClaim { get; init; } = "tenant_id";

    [Required]
    public string ApiKeyIdClaim { get; init; } = "api_key_id";

    public FixedWindowLimit Authenticated { get; init; } = new() { PermitLimit = 300 };

    public FixedWindowLimit Anonymous { get; init; } = new() { PermitLimit = 60 };

    // Opt in only when authentication produces the corresponding trusted claim.
    public FixedWindowLimit? Tenant { get; init; }
    public FixedWindowLimit? ApiKey { get; init; }

    [Required, StringLength(128), RegularExpression(@"^[a-zA-Z0-9:_-]+$")]
    public string KeyPrefix { get; init; } = "gateway:ratelimit";

    public bool FailOpen { get; init; } = true;

    [Range(50, 5_000)]
    public int TimeoutMilliseconds { get; init; } = 250;

    [Range(100, 30_000)]
    public int ConnectTimeoutMilliseconds { get; init; } = 2_000;

    [Range(1, 300)]
    public int FailureCooldownSeconds { get; init; } = 5;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(RedisConnectionString);

}
