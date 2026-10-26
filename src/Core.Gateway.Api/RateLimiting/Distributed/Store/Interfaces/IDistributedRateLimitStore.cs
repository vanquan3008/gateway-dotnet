namespace Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;

public interface IDistributedRateLimitStore
{
    bool IsConfigured { get; }
    ValueTask<DistributedRateLimitResult> AcquireAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken);
}
