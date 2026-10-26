using System.Diagnostics;
using System.Diagnostics.Metrics;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using static Core.Gateway.Api.RateLimiting.Shared.GatewayRateLimitingNames;

namespace Core.Gateway.Api.RateLimiting.Shared;

internal static class GatewayRateLimitingMetrics
{
    public const string MeterName = "Core.Gateway.RateLimiting";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> AllowedRequests = Meter.CreateCounter<long>(
        "gateway.ratelimit.distributed.allowed",
        unit: "{quota}",
        description: "Number of shared quota acquisitions allowed by Redis.");

    private static readonly Counter<long> RejectedRequests = Meter.CreateCounter<long>(
        "gateway.ratelimit.distributed.rejected",
        unit: "{quota}",
        description: "Number of shared quota acquisitions rejected by Redis.");

    private static readonly Counter<long> RedisUnavailable = Meter.CreateCounter<long>(
        "gateway.ratelimit.redis.unavailable",
        unit: "{quota}",
        description: "Number of distributed rate limit operations that failed because Redis was unavailable.");

    private static readonly Counter<long> FailOpenDecisions = Meter.CreateCounter<long>(
        "gateway.ratelimit.distributed.fail_open",
        unit: "{quota}",
        description: "Shared quota decisions taken by the local fallback when Redis was unavailable.");

    private static readonly Counter<long> LocalFallbackDecisions = Meter.CreateCounter<long>(
        "gateway.ratelimit.local.fallback",
        unit: "{quota}",
        description: "Quota decisions enforced by the per-pod fallback while Redis was unavailable.");

    private static readonly Counter<long> RouteDecisions = Meter.CreateCounter<long>(
        "gateway.ratelimit.route.decisions",
        unit: "{request}",
        description: "Route policy quota decisions by policy, result and counter store.");

    private static readonly Counter<long> AnonymousRejected = Meter.CreateCounter<long>(
        "gateway.ratelimit.anonymous.rejected",
        unit: "{request}",
        description: "Requests without an authenticated identity rejected by the per-IP local quota.");

    private static readonly Histogram<double> RedisDuration = Meter.CreateHistogram<double>(
        "gateway.ratelimit.redis.duration",
        unit: "ms",
        description: "Duration of Redis rate limit operations.");

    private static readonly Counter<long> RefundFailures = Meter.CreateCounter<long>(
        "gateway.ratelimit.redis.refund_failed",
        unit: "{quota}",
        description: "Rejected requests whose counted permit could not be returned because Redis was unavailable.");

    private static readonly Counter<long> HeartbeatFailures = Meter.CreateCounter<long>(
        "gateway.ratelimit.instances.heartbeat_failed",
        unit: "{heartbeat}",
        description: "Pod heartbeats that could not reach Redis; the fallback keeps its last known pod count.");

    /// <summary>
    /// Publishes the live pod count as a gauge.
    /// </summary>
    /// <param name="count">Reads the current count.</param>
    public static void ObserveActiveInstances(Func<int?> count) => Meter.CreateObservableGauge(
        "gateway.ratelimit.instances.active",
        () => count() is { } value ? [new Measurement<int>(value)] : Array.Empty<Measurement<int>>(),
        unit: "{instance}",
        description: "Live gateway pods as last counted through Redis; the local fallback splits each quota by this.");

    /// <summary>
    /// Counts a pod heartbeat that could not reach Redis.
    /// </summary>
    public static void RecordHeartbeatFailed() => HeartbeatFailures.Add(1);

    /// <summary>
    /// Counts a rejected request whose permit could not be returned.
    /// </summary>
    public static void RecordRefundFailed() => RefundFailures.Add(1);

    /// <summary>
    /// Counts a request rejected by the per-IP anonymous cap.
    /// </summary>
    public static void RecordAnonymousRejected() => AnonymousRejected.Add(1);

    /// <summary>
    /// Counts a route policy decision.
    /// </summary>
    /// <param name="policy">Policy name.</param>
    /// <param name="status">The outcome.</param>
    /// <param name="shared">True when Redis decided, false for the local fallback.</param>
    public static void RecordRouteDecision(string policy, DistributedRateLimitStatus status, bool shared)
    {
        var tags = new TagList
        {
            { MetricTag.Policy, policy },
            { MetricTag.Result, GetResultString(status) },
            { MetricTag.Store, shared ? MetricStore.Redis : MetricStore.Local }
        };

        RouteDecisions.Add(1, tags);
    }

    /// <summary>
    /// Counts a decision taken by the per-pod fallback.
    /// </summary>
    /// <param name="partitionType">The quota kind.</param>
    /// <param name="status">The outcome.</param>
    public static void RecordLocalFallback(string partitionType, DistributedRateLimitStatus status)
    {
        var tags = new TagList
        {
            { MetricTag.PartitionType, partitionType },
            { MetricTag.Result, status == DistributedRateLimitStatus.Rejected ? MetricResult.Rejected : MetricResult.Allowed }
        };

        LocalFallbackDecisions.Add(1, tags);
    }

    /// <summary>
    /// Records a Redis quota check: duration and outcome.
    /// </summary>
    /// <param name="partitionType">The quota kind.</param>
    /// <param name="status">The outcome.</param>
    /// <param name="elapsed">How long Redis took.</param>
    /// <param name="useLocalFallbackWhenStoreUnavailable">Whether the fallback is on.</param>
    /// <exception cref="ArgumentOutOfRangeException">The status is unknown.</exception>
    public static void RecordAcquisition(
        string partitionType,
        DistributedRateLimitStatus status,
        TimeSpan elapsed,
        bool useLocalFallbackWhenStoreUnavailable)
    {
        var tags = new TagList
        {
            { MetricTag.PartitionType, partitionType }
        };

        RedisDuration.Record(elapsed.TotalMilliseconds, tags);

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
                if (useLocalFallbackWhenStoreUnavailable)
                {
                    FailOpenDecisions.Add(1, tags);
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown distributed rate limit status.");
        }
    }

    /// <summary>
    /// The metric tag for an outcome.
    /// </summary>
    /// <param name="status">The outcome.</param>
    /// <returns>allowed, rejected or unavailable.</returns>
    private static string GetResultString(DistributedRateLimitStatus status) => status switch
    {
        DistributedRateLimitStatus.Rejected => MetricResult.Rejected,
        DistributedRateLimitStatus.Unavailable => MetricResult.Unavailable,
        _ => MetricResult.Allowed
    };
}
