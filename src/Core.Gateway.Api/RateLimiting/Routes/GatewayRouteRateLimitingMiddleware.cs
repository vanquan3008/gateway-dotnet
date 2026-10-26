using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting.Routes;

internal sealed class GatewayRouteRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayRateLimitingOptions _settings;

    /// <summary>
    /// Creates the middleware.
    /// </summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="options">The rate limiting options.</param>
    public GatewayRouteRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayRateLimitingOptions> options)
    {
        _next = next;
        _settings = options.Value;
    }

    /// <summary>
    /// Applies the route's RateLimitPolicy quota (shared in Redis, per-pod fallback when Redis is down). Routes without a known policy pass through.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="store">The shared quota store.</param>
    /// <param name="localQuota">The per-pod fallback quota.</param>
    /// <returns>A task that completes when the request was forwarded or rejected.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        IDistributedRateLimitStore store,
        LocalFallbackQuota localQuota)
    {
        if (!_settings.Enabled)
        {
            await _next(context);
            return;
        }

        var policyName = context.GetRouteMetadata(GatewayRouteMetadata.RateLimitPolicy);

        if (policyName == null)
        {
            await _next(context);
            return;
        }

        if (!_settings.Policies.TryGetValue(
            policyName,
            out var policy))
        {
            await _next(context);
            return;
        }

        var partition = GatewayRateLimitPartition.ForRoutePolicy(
            context,
            policyName,
            policy,
            _settings.Distributed);

        string partitionKey = partition.Key;
        int permitLimit = partition.Limit.PermitLimit;
        TimeSpan window = partition.Limit.Window;

        var result = await store.AcquireAsync(
            partitionKey,
            permitLimit,
            window,
            context.RequestAborted);

        bool isSharedResult =
            result.Status != DistributedRateLimitStatus.Unavailable;

        if (!isSharedResult)
        {
            result = localQuota.Resolve(partition, store.IsConfigured, result.RetryAfter);
        }
        else
        {
            GatewayRateLimitHeaders.Observe(context, result);
        }

        GatewayRateLimitingMetrics.RecordRouteDecision(
            policyName,
            result.Status,
            isSharedResult);

        if (result.Status == DistributedRateLimitStatus.Unavailable)
        {
            await GatewayRateLimitResponse.WriteStoreUnavailableAsync(context, result.RetryAfter);
            return;
        }

        if (result.Status == DistributedRateLimitStatus.Rejected)
        {
            await GatewayRateLimitResponse
                .WriteQuotaExceededAsync(
                    context,
                    result.RetryAfter);

            return;
        }

        await _next(context);
    }
}
