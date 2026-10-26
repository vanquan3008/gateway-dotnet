using System.ComponentModel.DataAnnotations;

namespace Core.Gateway.Api.RateLimiting.Options;

public sealed class GatewayRateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; init; } = true;

    [Required]
    public LocalRateLimitingOptions Local { get; init; } = new();

    [Required]
    public DistributedRateLimitingOptions Distributed { get; init; } = new();
}

