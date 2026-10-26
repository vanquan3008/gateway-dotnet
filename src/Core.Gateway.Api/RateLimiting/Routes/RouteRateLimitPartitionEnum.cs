namespace Core.Gateway.Api.RateLimiting.Routes;

public enum RouteRateLimitPartition
{
    Client,
    Ip,
    Tenant,
    ApiKey
}
