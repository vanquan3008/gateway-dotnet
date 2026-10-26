
using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.RateLimiting.Local;

internal sealed class GatewayAnonymousRateLimitingMiddleware
    : IMiddleware, IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext>? _limiter;
    private readonly TimeSpan _window;
    /// <summary>
    /// Builds the per-IP limiter when Local:Anonymous is configured.
    /// </summary>
    /// <param name="options">The rate limiting options.</param>
    public GatewayAnonymousRateLimitingMiddleware(
        IOptions<GatewayRateLimitingOptions> options)
    {
        var settings = options.Value;

        if (!settings.Enabled || settings.Local?.Enabled != true || settings.Local.Anonymous == null)
        {
            _limiter = null;
            return;
        }

        var anonymousLimit = settings.Local.Anonymous;
        _window = anonymousLimit.Window;
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            context =>
            {
                var partition = GatewayRateLimitPartition.ForAnonymous(
                    context,
                    anonymousLimit);
                return RateLimitPartition.GetSlidingWindowLimiter(
                    partition.Key, _ => anonymousLimit.ToSlidingWindow());
            });
    }

    /// <summary>
    /// Per-pod cap per IP for unauthenticated requests, applied before Redis to absorb floods.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <returns>A task that completes when the request was forwarded or rejected with 429.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        RequestDelegate next)
    {
        if (_limiter == null)
        {
            await next(context);
            return;
        }
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<DisableRateLimitingAttribute>() != null)
        {
            await next(context);
            return;
        }
        using RateLimitLease lease = _limiter.AttemptAcquire(context);
        if (lease.IsAcquired)
        {
            await next(context);
            return;
        }
        GatewayRateLimitingMetrics.RecordAnonymousRejected();

        // A sliding window lease carries no RetryAfter, so tell the client to wait one full window.
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
            ? retryAfterValue
            : _window;
        await GatewayRateLimitResponse.WriteQuotaExceededAsync(context, retryAfter);
    }

    /// <summary>
    /// Releases the limiter.
    /// </summary>
    public void Dispose()
    {
        _limiter?.Dispose();
    }
}
