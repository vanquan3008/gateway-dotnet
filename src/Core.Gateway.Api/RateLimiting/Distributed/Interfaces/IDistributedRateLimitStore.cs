namespace Core.Gateway.Api.RateLimiting.Distributed.Interfaces;

public interface IDistributedRateLimitStore
{
    ValueTask<DistributedRateLimitResult> AcquireAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken);
}
