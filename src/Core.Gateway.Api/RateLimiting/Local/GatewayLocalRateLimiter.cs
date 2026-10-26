using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Shared;
using Yarp.ReverseProxy.Model;
using static Core.Gateway.Api.RateLimiting.Shared.GatewayRateLimitingNames;

namespace Core.Gateway.Api.RateLimiting.Local;

internal static class GatewayLocalRateLimiter
{
    /// <summary>
    /// Per-pod request-rate limiter: fixed-window limits per backend cluster and per pod, both must pass.
    /// </summary>
    /// <param name="settings">The instance and service quotas.</param>
    /// <returns>The global limiter.</returns>
    /// <exception cref="ArgumentNullException">settings is null.</exception>
    public static PartitionedRateLimiter<HttpContext> Create(
        LocalRateLimitingOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PartitionedRateLimiter<HttpContext> serviceRateLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context =>
                {
                    string serviceKey = GetServiceKey(context);
                    return CreateFixedWindowPartition(
                        serviceKey,
                        settings.Service);
                });

        PartitionedRateLimiter<HttpContext> instanceRateLimiter =
            PartitionedRateLimiter.Create<HttpContext, string>(
                _ =>
                {
                    string instanceKey = LocalPartition.Instance;

                    return CreateFixedWindowPartition(
                        instanceKey,
                        settings.Instance);
                });

        return PartitionedRateLimiter.CreateChained(
            serviceRateLimiter,
            instanceRateLimiter);
    }

    /// <summary>
    /// Per-pod in-flight limiter per backend cluster and per pod, with no queue.
    /// </summary>
    /// <param name="settings">The concurrency limits.</param>
    /// <returns>The concurrency limiter.</returns>
    /// <exception cref="ArgumentNullException">settings is null.</exception>
    public static PartitionedRateLimiter<HttpContext> CreateConcurrencyLimiter(
        LocalRateLimitingOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        PartitionedRateLimiter<HttpContext> serviceConcurrencyLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context =>
                {
                    string serviceKey = GetServiceKey(context);
                    return CreateConcurrencyPartition(
                        serviceKey,
                        settings.ServiceConcurrencyLimit);
                });

        PartitionedRateLimiter<HttpContext> instanceConcurrencyLimiter =
            PartitionedRateLimiter.Create<HttpContext, string>(
                _ =>
                {
                    string instanceKey = LocalPartition.Instance;
                    return CreateConcurrencyPartition(
                        instanceKey,
                        settings.InstanceConcurrencyLimit);
                });

        return PartitionedRateLimiter.CreateChained(
            serviceConcurrencyLimiter,
            instanceConcurrencyLimiter);
    }

    /// <summary>
    /// Backend cluster id of the request, or "unmatched" when no route matched.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>The cluster id.</returns>
    /// <exception cref="ArgumentNullException">context is null.</exception>
    internal static string GetServiceKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RouteModel? routeModel = context.GetEndpoint()?
            .Metadata
            .GetMetadata<RouteModel>();

        string? clusterId = routeModel?.Config.ClusterId;

        if (string.IsNullOrWhiteSpace(clusterId))
        {
            return LocalPartition.UnmatchedCluster;
        }

        return clusterId;
    }

    /// <summary>
    /// A fixed window partition for a key.
    /// </summary>
    /// <param name="partitionKey">The key.</param>
    /// <param name="limit">The quota.</param>
    /// <returns>The partition.</returns>
    private static RateLimitPartition<string> CreateFixedWindowPartition(
        string partitionKey,
        QuotaLimit limit)
    {
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                QueueLimit = limit.QueueLimit,
                Window = limit.Window,
                AutoReplenishment = true
            });
    }

    /// <summary>
    /// A concurrency partition for a key, with no queue.
    /// </summary>
    /// <param name="partitionKey">The key.</param>
    /// <param name="permitLimit">Requests allowed at once.</param>
    /// <returns>The partition.</returns>
    private static RateLimitPartition<string> CreateConcurrencyPartition(
        string partitionKey,
        int permitLimit)
    {
        return RateLimitPartition.GetConcurrencyLimiter(
            partitionKey,
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = permitLimit,
                QueueLimit = 0
            });
    }
}
