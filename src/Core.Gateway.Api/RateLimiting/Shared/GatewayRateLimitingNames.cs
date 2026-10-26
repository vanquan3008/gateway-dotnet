namespace Core.Gateway.Api.RateLimiting.Shared;

internal static class GatewayRateLimitingNames
{
    public static class PartitionType
    {
        public const string User = "user";
        public const string Tenant = "tenant";
        public const string ApiKey = "api-key";
        public const string Ip = "ip";
        public const string Route = "route";
    }

    public static class PartitionKey
    {
        public const char Separator = ':';
        public const string FallbackType = "partition";
        public const string UnknownClientIp = "unknown";
        public const string Ipv6PrefixSuffix = "/64";
        public const string RoutePrefix = "route";
        public const string ReadinessProbe = "probe:readiness";
    }

    public static class LocalPartition
    {
        public const string Instance = "instance";
        public const string UnmatchedCluster = "unmatched";
    }

    public static class MetricTag
    {
        public const string PartitionType = "partition.type";
        public const string Policy = "policy";
        public const string Result = "result";
        public const string Store = "store";
        public const string Cluster = "cluster";
    }

    public static class MetricResult
    {
        public const string Allowed = "allowed";
        public const string Rejected = "rejected";
        public const string Unavailable = "unavailable";
    }

    public static class MetricStore
    {
        public const string Redis = "redis";
        public const string Local = "local";
    }

    public static class HealthCheck
    {
        public const string RateLimitStore = "rate-limit-store";
    }
}
