using System.Diagnostics;
using Core.Gateway.Api.RateLimiting.Distributed.Interfaces;
using Core.Gateway.Api.RateLimiting.Options;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting.Distributed;

public sealed class DistributedRateLimitingMiddleware(
    RequestDelegate next, IOptions<GatewayRateLimitingOptions> options)
{
    public const string MeterName = DistributedRateLimitingMetrics.MeterName;
    private readonly GatewayRateLimitingOptions _settings = options.Value;
    public async Task InvokeAsync(HttpContext context, IDistributedRateLimitStore store)
    {
        if (ShouldSkipDistributedQuota(context))
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

    private bool ShouldSkipDistributedQuota(HttpContext context)
    {
        return !_settings.Enabled
            || !_settings.Distributed.Enabled
            || context.GetEndpoint()?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null;
    }

    private async Task<bool> AcquireQuotaAsync(HttpContext context, IDistributedRateLimitStore store,
        GatewayRateLimitPartition partition)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await store.AcquireAsync(partition.Key, partition.Limit.PermitLimit,
            partition.Limit.Window, context.RequestAborted);

        DistributedRateLimitingMetrics.RecordAcquisition(partition.Type, result.Status,
            Stopwatch.GetElapsedTime(started), _settings.Distributed.FailOpen);

        switch (result.Status)
        {
            case DistributedRateLimitStatus.Allowed:
                return true;

            case DistributedRateLimitStatus.Rejected:
                await GatewayRateLimitResponse.WriteQuotaExceededAsync(context, result.RetryAfter);
                return false;

            case DistributedRateLimitStatus.Unavailable:
                if (_settings.Distributed.FailOpen)
                {
                    return true;
                }

                await GatewayRateLimitResponse.WriteStoreUnavailableAsync(context);
                return false;

            default:
                throw new InvalidOperationException("The distributed rate limit store returned an unknown status.");
        }
    }
}
