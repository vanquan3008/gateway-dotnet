using System.Diagnostics;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting.Distributed;

/// <summary>
/// Applies the shared Redis quotas per client, tenant and API key.
/// </summary>
public sealed class DistributedRateLimitingMiddleware(
    RequestDelegate next, IOptions<GatewayRateLimitingOptions> options)
{
    private readonly GatewayRateLimitingOptions _settings = options.Value;
    /// <summary>
    /// Applies the shared Redis quotas in order: client (user or IP), tenant, API key. The first rejection ends the request with 429.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="store">The shared quota store.</param>
    /// <returns>A task that completes when the request was forwarded or rejected.</returns>
    public async Task InvokeAsync(HttpContext context, IDistributedRateLimitStore store)
    {
        if (!_settings.Enabled || !_settings.Distributed.Enabled || context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
        {
            await next(context);
            return;
        }
        var clientQuota = GatewayRateLimitPartition.ForClient(context, _settings.Distributed);
        if (!await AcquireQuotaAsync(context, store, clientQuota))
        {
            return;
        }
        if (GatewayRateLimitPartition.ForTenant(context, _settings.Distributed) is { } tenant &&
            !await AcquireQuotaAsync(context, store, tenant))
            return;

        if (GatewayRateLimitPartition.ForApiKey(context, _settings.Distributed) is { } apiKey &&
            !await AcquireQuotaAsync(context, store, apiKey))
            return;

        await next(context);
    }

    /// <summary>
    /// Takes one permit from one quota: continues if allowed, 429 if rejected, local fallback or 503 if Redis is down.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="store">The shared quota store.</param>
    /// <param name="partition">The quota to consume.</param>
    /// <returns>True when the request may continue; false when a response was already written.</returns>
    /// <exception cref="InvalidOperationException">The store returned an unknown status.</exception>
    private async Task<bool> AcquireQuotaAsync(HttpContext context, IDistributedRateLimitStore store,
        GatewayRateLimitPartition partition)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await store.AcquireAsync(partition.Key, partition.Limit.PermitLimit,
            partition.Limit.Window, context.RequestAborted);

        GatewayRateLimitingMetrics.RecordAcquisition(partition.Type, result.Status,
            Stopwatch.GetElapsedTime(started), _settings.Distributed.Fallback.UseLocalFallbackWhenStoreUnavailable);

        GatewayRateLimitHeaders.Observe(context, result);

        switch (result.Status)
        {
            case DistributedRateLimitStatus.Allowed:
                return true;

            case DistributedRateLimitStatus.Rejected:
                await GatewayRateLimitResponse.WriteQuotaExceededAsync(context, result.RetryAfter);
                return false;

            case DistributedRateLimitStatus.Unavailable:
                var fallbackResult = context.RequestServices.GetRequiredService<LocalFallbackQuota>()
                    .Resolve(partition, store.IsConfigured, result.RetryAfter);
                if (fallbackResult.Status == DistributedRateLimitStatus.Unavailable)
                {
                    await GatewayRateLimitResponse.WriteStoreUnavailableAsync(context, fallbackResult.RetryAfter);
                    return false;
                }

                GatewayRateLimitingMetrics.RecordLocalFallback(partition.Type, fallbackResult.Status);
                if (fallbackResult.Status != DistributedRateLimitStatus.Rejected)
                    return true;

                await GatewayRateLimitResponse.WriteQuotaExceededAsync(context, fallbackResult.RetryAfter);
                return false;

            default:
                throw new InvalidOperationException("The distributed rate limit store returned an unknown status.");
        }
    }
}
