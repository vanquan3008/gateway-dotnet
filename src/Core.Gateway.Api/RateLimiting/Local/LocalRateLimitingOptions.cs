using System.ComponentModel.DataAnnotations;
using Core.Gateway.Api.RateLimiting.Shared;

namespace Core.Gateway.Api.RateLimiting.Local;

public sealed class LocalRateLimitingOptions
{
    public bool Enabled { get; init; } = true;
    public QuotaLimit Instance { get; init; } = new() { PermitLimit = 1_000, WindowSeconds = 1 };
    public QuotaLimit Service { get; init; } = new() { PermitLimit = 600, WindowSeconds = 1 };

    public QuotaLimit? Anonymous { get; init; }

    [Range(1, 100_000)]
    public int InstanceConcurrencyLimit { get; init; } = 1_024;

    [Range(1, 100_000)]
    public int ServiceConcurrencyLimit { get; init; } = 256;
}
