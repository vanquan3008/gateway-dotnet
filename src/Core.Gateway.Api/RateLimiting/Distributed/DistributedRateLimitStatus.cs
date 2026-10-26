namespace Core.Gateway.Api.RateLimiting.Distributed;

public enum DistributedRateLimitStatus
{
    Unavailable,
    Allowed,
    Rejected
}
