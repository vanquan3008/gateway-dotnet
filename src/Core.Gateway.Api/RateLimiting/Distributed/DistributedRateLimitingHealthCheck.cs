using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using static Core.Gateway.Api.RateLimiting.Shared.GatewayRateLimitingNames;

namespace Core.Gateway.Api.RateLimiting.Distributed;

/// <summary>
/// Readiness check for the shared quota store.
/// </summary>
public sealed class DistributedRateLimitingHealthCheck(
    IOptions<GatewayRateLimitingOptions> options, IDistributedRateLimitStore store) : IHealthCheck
{
    /// <summary>
    /// Readiness check for the shared quota store: Healthy, Degraded (local fallback) or Unhealthy (fail closed).
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The store health.</returns>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.Distributed.Enabled)
        {
            return HealthCheckResult.Healthy("Distributed rate limiting is disabled.");
        }
        var result = await store.AcquireAsync(PartitionKey.ReadinessProbe, int.MaxValue, TimeSpan.FromSeconds(1), cancellationToken);
        if (result.Status != DistributedRateLimitStatus.Unavailable)
        {
            return HealthCheckResult.Healthy("Shared Redis quotas are available.");
        }

        if (settings.Distributed.Fallback.UseLocalFallbackWhenStoreUnavailable)
        {
            return HealthCheckResult.Degraded("Redis unavailable; shared quotas are decided by the local fallback.");
        }
        return HealthCheckResult.Unhealthy("Redis unavailable; shared quota requests fail closed (503).");
    }
}
