using System.ComponentModel.DataAnnotations;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.RateLimiting.Routes;

namespace Core.Gateway.Api.RateLimiting;

public sealed class GatewayRateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; init; } = true;

    [Required]
    public LocalRateLimitingOptions Local { get; init; } = new();

    [Required]
    public DistributedRateLimitingOptions Distributed { get; init; } = new();

    public Dictionary<string, RouteRateLimitPolicyOptions> Policies { get; init; } = [];
}

