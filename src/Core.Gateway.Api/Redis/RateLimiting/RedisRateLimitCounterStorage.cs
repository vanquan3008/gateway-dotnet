using System.Security.Cryptography;
using System.Text;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using StackExchange.Redis;
using static Core.Gateway.Api.RateLimiting.Shared.GatewayRateLimitingNames;

namespace Core.Gateway.Api.Redis.RateLimiting;

/// <summary>
/// Redis storage for the sliding window counters.
/// </summary>
internal sealed class RedisRateLimitCounterStorage(IRedisConnection redis, string keyPrefix) : IRateLimitCounterStorage
{
    /// <summary>
    /// Counts one request in the current window and reads the previous window, in one Redis batch.
    /// </summary>
    /// <param name="partitionKey">Identifies the quota.</param>
    /// <param name="windowMilliseconds">Window length.</param>
    /// <param name="windowIndex">Which window now falls in.</param>
    /// <param name="cancellationToken">Cancels the Redis call.</param>
    /// <returns>Current and previous window counts.</returns>
    /// <exception cref="RedisUnavailableException">Redis could not be reached.</exception>
    public ValueTask<RateLimitCounter> IncrementAsync(
        string partitionKey,
        long windowMilliseconds,
        long windowIndex,
        CancellationToken cancellationToken)
    {
        var baseKey = BaseKey(partitionKey, windowMilliseconds);
        var lifetime = TimeSpan.FromMilliseconds(2 * windowMilliseconds);

        return redis.ExecuteAsync(async database =>
        {
            var currentKey = WindowKey(baseKey, windowIndex);
            var batch = database.CreateBatch();
            var current = batch.StringIncrementAsync(currentKey);
            var expire = batch.KeyExpireAsync(currentKey, lifetime);
            var previous = batch.StringGetAsync(WindowKey(baseKey, windowIndex - 1));
            batch.Execute();

            await Task.WhenAll(current, expire, previous);
            return new RateLimitCounter(current.Result, previous.Result.IsNull ? 0 : (long)previous.Result);
        }, cancellationToken);
    }

    /// <summary>
    /// Returns the permit of a rejected request.
    /// </summary>
    /// <param name="partitionKey">Identifies the quota.</param>
    /// <param name="windowMilliseconds">Window length.</param>
    /// <param name="windowIndex">Window the permit was counted in.</param>
    /// <returns>False when Redis was unreachable and the permit was not returned.</returns>
    public async ValueTask<bool> DecrementAsync(string partitionKey, long windowMilliseconds, long windowIndex)
    {
        var key = WindowKey(BaseKey(partitionKey, windowMilliseconds), windowIndex);
        try
        {
            return await redis.ExecuteAsync(database =>
            {
                database.StringDecrement(key, flags: CommandFlags.FireAndForget);
                return Task.FromResult(true);
            }, CancellationToken.None);
        }
        catch (RedisUnavailableException)
        {
            return false;
        }
    }

    /// <summary>
    /// Key prefix of a quota: partition type and a hash of the partition key.
    /// </summary>
    /// <param name="partitionKey">Identifies the quota.</param>
    /// <param name="windowMilliseconds">Window length.</param>
    /// <returns>The base key.</returns>
    private string BaseKey(string partitionKey, long windowMilliseconds)
    {
        var separatorIndex = partitionKey.IndexOf(PartitionKey.Separator);
        var partitionType = separatorIndex > 0 ? partitionKey[..separatorIndex] : PartitionKey.FallbackType;
        var identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partitionKey)));
        return $"{keyPrefix}:sw:{partitionType}:{identityHash}:{windowMilliseconds}";
    }

    /// <summary>
    /// Key of one window.
    /// </summary>
    /// <param name="baseKey">The base key.</param>
    /// <param name="windowIndex">The window.</param>
    /// <returns>The window key.</returns>
    private static string WindowKey(string baseKey, long windowIndex) => $"{baseKey}:{windowIndex}";
}
