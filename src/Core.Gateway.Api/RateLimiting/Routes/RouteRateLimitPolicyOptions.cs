using System.ComponentModel.DataAnnotations;
using Core.Gateway.Api.RateLimiting.Shared;

namespace Core.Gateway.Api.RateLimiting.Routes;

public sealed class RouteRateLimitPolicyOptions
{
    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; }

    [Range(1, 86_400)]
    public int WindowSeconds { get; init; } = 60;

    [EnumDataType(typeof(RouteRateLimitPartition))]
    public RouteRateLimitPartition Partition { get; init; } = RouteRateLimitPartition.Client;

    /// <summary>
    /// The policy as a quota.
    /// </summary>
    /// <returns>The quota.</returns>
    public QuotaLimit ToLimit() => new()
    {
        PermitLimit = PermitLimit,
        WindowSeconds = WindowSeconds
    };
}
