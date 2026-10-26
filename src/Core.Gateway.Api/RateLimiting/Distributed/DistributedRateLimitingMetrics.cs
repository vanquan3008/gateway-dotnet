using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Core.Gateway.Api.RateLimiting.Distributed;

internal static class DistributedRateLimitingMetrics
{
    public const string MeterName = "Core.Gateway.RateLimiting";
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> AllowedRequests =
        Meter.CreateCounter<long>(
            "gateway.ratelimit.distributed.allowed",
            unit: "{quota}",
            description: "Number of shared quota acquisitions allowed by Redis.");

    private static readonly Counter<long> RejectedRequests =
        Meter.CreateCounter<long>(
            "gateway.ratelimit.distributed.rejected",
            unit: "{quota}",
            description: "Number of shared quota acquisitions rejected by Redis.");

    private static readonly Counter<long> RedisUnavailable =
        Meter.CreateCounter<long>(
            "gateway.ratelimit.redis.unavailable",
            unit: "{quota}",
            description: "Number of distributed rate limit operations that failed because Redis was unavailable.");

    private static readonly Counter<long> BypassedQuotas =
        Meter.CreateCounter<long>(
            "gateway.ratelimit.distributed.bypassed",
            unit: "{quota}",
            description: "Number of shared quota acquisitions bypassed by FailOpen policy when Redis was unavailable.");

    private static readonly Histogram<double> RedisDuration =
        Meter.CreateHistogram<double>(
            "gateway.ratelimit.redis.duration",
            unit: "ms",
            description: "Duration of Redis rate limit operations.");

    public static void RecordAcquisition(
        string partitionType,
        DistributedRateLimitStatus status,
        TimeSpan elapsed,
        bool failOpen)
    {
        var tags = new TagList
        {
            { "partition.type", partitionType }
        };

        RedisDuration.Record(
            elapsed.TotalMilliseconds,
            tags);

        switch (status)
        {
            case DistributedRateLimitStatus.Allowed:
                AllowedRequests.Add(1, tags);
                break;

            case DistributedRateLimitStatus.Rejected:
                RejectedRequests.Add(1, tags);
                break;

            case DistributedRateLimitStatus.Unavailable:
                RedisUnavailable.Add(1, tags);
                if (failOpen) BypassedQuotas.Add(1, tags);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(status),
                    status,
                    "Unknown distributed rate limit status.");
        }
    }

}
