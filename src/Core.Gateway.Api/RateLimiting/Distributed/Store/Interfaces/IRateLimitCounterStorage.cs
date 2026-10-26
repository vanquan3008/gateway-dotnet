namespace Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;

internal interface IRateLimitCounterStorage
{
    ValueTask<RateLimitCounter> IncrementAsync(
        string partitionKey,
        long windowMilliseconds,
        long windowIndex,
        CancellationToken cancellationToken);
    ValueTask<bool> DecrementAsync(string partitionKey, long windowMilliseconds, long windowIndex);
}
