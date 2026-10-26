using Core.Gateway.Api.RateLimiting.Distributed.Interfaces;
using Core.Gateway.Api.RateLimiting.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting.Distributed;

public sealed class DistributedRateLimitingHealthCheck(
    IOptions<GatewayRateLimitingOptions> options, IDistributedRateLimitStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.Distributed.Enabled)
        {
            return HealthCheckResult.Healthy("Distributed rate limiting is disabled.");
        }
        var result = await store.AcquireAsync("probe:readiness", int.MaxValue, TimeSpan.FromSeconds(1), cancellationToken);
        if (result.Status != DistributedRateLimitStatus.Unavailable)
        {
            return HealthCheckResult.Healthy("Shared Redis quotas are available.");
        }

        if (settings.Distributed.FailOpen)
        {
            return HealthCheckResult.Degraded("Redis unavailable; shared quotas are bypassed by FailOpen policy.");
        }
        return HealthCheckResult.Unhealthy("Redis unavailable; shared quota requests fail closed (503).");
    }
}
