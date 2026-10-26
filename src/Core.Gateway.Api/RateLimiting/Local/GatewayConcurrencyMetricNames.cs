namespace Core.Gateway.Api.RateLimiting.Local;

internal static class GatewayConcurrencyMetricNames
{
    public const string MeterName = "Core.Gateway.Concurrency";
    public const string ActiveMetricName = "gateway.concurrency.active";
    public const string RejectedMetricName = "gateway.concurrency.rejected";
    public const string DurationMetricName = "gateway.concurrency.duration";
    public const string DurationMetricUnit = "s";
}
