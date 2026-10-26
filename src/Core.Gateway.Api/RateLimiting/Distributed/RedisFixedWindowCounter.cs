using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Core.Gateway.Api.RateLimiting.Distributed;

internal static class RedisFixedWindowCounter
{
    // The window starts at its first acquisition. Redis TTL owns the clock.
    // One atomic script checks the quota, increments it and sets/repairs expiration.
    private const string AcquireScript = """
        local count = tonumber(redis.call('GET', KEYS[1]) or '0')
        local ttl = redis.call('PTTL', KEYS[1])
        if count >= tonumber(ARGV[1]) then
            if ttl < 0 then
                redis.call('PEXPIRE', KEYS[1], ARGV[2])
                ttl = tonumber(ARGV[2])
            end
            return {0, ttl}
        end
        count = redis.call('INCR', KEYS[1])
        if count == 1 or ttl < 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[2])
            ttl = tonumber(ARGV[2])
        end
        return {1, ttl}
        """;

    public static async Task<DistributedRateLimitResult> AcquireAsync(IDatabase database,
        string keyPrefix, string partitionKey, int permitLimit, TimeSpan window)
    {
        var windowMilliseconds = checked((long)window.TotalMilliseconds);
        var counterKey = CreateCounterKey(keyPrefix, partitionKey, windowMilliseconds);
        var response = await database.ScriptEvaluateAsync(
            AcquireScript, [counterKey], [permitLimit, windowMilliseconds], CommandFlags.DemandMaster);

        var values = (RedisResult[])response!;
        var retryAfter = TimeSpan.FromMilliseconds(Math.Max(1, (long)values[1]));

        return (long)values[0] == 1
            ? DistributedRateLimitResult.Allowed(retryAfter)
            : DistributedRateLimitResult.Rejected(retryAfter);
    }

    private static string CreateCounterKey(string keyPrefix, string partitionKey, long windowMilliseconds)
    {
        var separatorIndex = partitionKey.IndexOf(':');
        var partitionType = separatorIndex > 0 ? partitionKey[..separatorIndex] : "partition";
        var identityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partitionKey)));

        return $"{keyPrefix}:{partitionType}:{identityHash}:{windowMilliseconds}";
    }
}
