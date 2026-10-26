using System.Threading.RateLimiting;
using Core.Gateway.Api.RateLimiting.Options;
using Core.Gateway.Api.RateLimiting.Shared;
using Yarp.ReverseProxy.Model;

namespace Core.Gateway.Api.RateLimiting.Local;

internal static class GatewayLocalRateLimiter
{
    public static PartitionedRateLimiter<HttpContext> Create(LocalRateLimitingOptions settings)
    {
        var instanceRateLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
            CreateFixedWindowPartition("instance", settings.Instance));
        var anonymousRateLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            settings.Anonymous is { } limit && context.User.Identity?.IsAuthenticated != true
                ? CreateFixedWindowPartition(GatewayRateLimitPartition.ForAnonymous(context, limit).Key, limit)
                : RateLimitPartition.GetNoLimiter("none"));
        var serviceRateLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            CreateFixedWindowPartition(GetServiceKey(context), settings.Service));
        // Reject an IP or service before spending the shared pod rate budget.
        return PartitionedRateLimiter.CreateChained(
            anonymousRateLimiter,
            serviceRateLimiter,
            instanceRateLimiter);
    }

    public static PartitionedRateLimiter<HttpContext> CreateConcurrencyLimiter(LocalRateLimitingOptions settings)
    {
        var service = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            CreateConcurrencyPartition(GetServiceKey(context), settings.ServiceConcurrencyLimit));
        var instance = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
            CreateConcurrencyPartition("instance", settings.InstanceConcurrencyLimit));

        return PartitionedRateLimiter.CreateChained(service, instance);
    }

    internal static string GetServiceKey(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.ClusterId ?? "unmatched";

    private static RateLimitPartition<string> CreateConcurrencyPartition(string key, int permitLimit) =>
        RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = permitLimit,
            QueueLimit = 0
        });

    private static RateLimitPartition<string> CreateFixedWindowPartition(string partitionKey, FixedWindowLimit limit) =>
        RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = limit.PermitLimit,
            QueueLimit = limit.QueueLimit,
            Window = limit.Window
        });
}
