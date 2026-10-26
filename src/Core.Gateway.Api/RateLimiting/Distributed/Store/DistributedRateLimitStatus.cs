namespace Core.Gateway.Api.RateLimiting.Distributed.Store;

public enum DistributedRateLimitStatus
{
    Unavailable,
    Allowed,
    Rejected
}
