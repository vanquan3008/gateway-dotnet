using System.ComponentModel.DataAnnotations;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Shared;

namespace Core.Gateway.Api.RateLimiting.Distributed;

public sealed class DistributedRateLimitingOptions
{
    public bool Enabled { get; init; }
    [Required]
    public string UserIdClaim { get; init; } = "sub";
    [Required]
    public string TenantIdClaim { get; init; } = "tenant_id";
    [Required]
    public string ApiKeyIdClaim { get; init; } = "api_key_id";
    public QuotaLimit Authenticated { get; init; } = new() { PermitLimit = 300 };
    public QuotaLimit Anonymous { get; init; } = new() { PermitLimit = 60 };
    public QuotaLimit? Tenant { get; init; }
    public QuotaLimit? ApiKey { get; init; }
    public DistributedFallbackOptions Fallback { get; init; } = new();
}
