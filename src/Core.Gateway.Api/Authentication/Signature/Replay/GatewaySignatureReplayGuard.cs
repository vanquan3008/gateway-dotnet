using System.Security.Cryptography;
using System.Text;
using Core.Gateway.Api.Authentication.Metrics;
using Core.Gateway.Api.Redis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Core.Gateway.Api.Authentication.Signature.Replay;

/// <summary>
/// Rejects a signature that was already used: in memory per pod and in Redis across pods.
/// </summary>
internal sealed class GatewaySignatureReplayGuard(
    IOptions<GatewaySignatureOptions> signatureOptions,
    IOptions<RedisOptions> redisOptions,
    IRedisConnection redis,
    ILogger<GatewaySignatureReplayGuard> logger) : IDisposable
{
    private const long MemoryFullLogIntervalMilliseconds = 60_000;

    private readonly TimeSpan _retention = TimeSpan.FromSeconds(signatureOptions.Value.MaxClockSkewSeconds * 2L);
    private readonly MemoryCache _seen = new(new MemoryCacheOptions { SizeLimit = signatureOptions.Value.ReplayMemoryEntries });
    private readonly object _seenLock = new();
    private long _retryAtMilliseconds;
    private long _memoryFullLoggedAtMilliseconds = -MemoryFullLogIntervalMilliseconds;

    /// <summary>
    /// Remembers a signature and says whether it is new. Without Redis, or while Redis is down, replays are only detected per pod.
    /// </summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="signature">The signature.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>True when the signature was not seen before.</returns>
    public async ValueTask<bool> TryRegisterAsync(string clientId, string signature, CancellationToken cancellationToken)
    {
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        var key = $"{clientId}:{fingerprint}";

        lock (_seenLock)
        {
            if (_seen.TryGetValue(key, out _))
            {
                return false;
            }

            _seen.Set(key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = _retention });
            if (!_seen.TryGetValue(key, out _))
            {
                ReportMemoryFull();
            }
        }

        if (!redis.IsConfigured || Environment.TickCount64 < Volatile.Read(ref _retryAtMilliseconds))
        {
            return true;
        }

        var settings = redisOptions.Value;

        try
        {
            return await redis.ExecuteAsync(
                database => database.StringSetAsync($"{settings.KeyPrefix}:signature-replay:{key}", 1, _retention, When.NotExists),
                cancellationToken);
        }
        catch (RedisUnavailableException exception)
        {
            Volatile.Write(ref _retryAtMilliseconds, Environment.TickCount64 + settings.FailureCooldownSeconds * 1_000L);
            logger.LogWarning(
                "Signature replay store unavailable ({Reason}); replays are only detected per pod until it recovers.",
                exception.Reason);
            return true;
        }
    }

    /// <summary>
    /// Counts the full-memory event and logs it at most once a minute.
    /// </summary>
    private void ReportMemoryFull()
    {
        GatewayAuthenticationMetrics.SignatureReplayMemoryFull();

        var now = Environment.TickCount64;
        var lastLogged = Volatile.Read(ref _memoryFullLoggedAtMilliseconds);
        if (now - lastLogged < MemoryFullLogIntervalMilliseconds ||
            Interlocked.CompareExchange(ref _memoryFullLoggedAtMilliseconds, now, lastLogged) != lastLogged)
        {
            return;
        }

        logger.LogWarning(
            "Signature replay memory is full ({Limit} entries); accepted signatures are not remembered on this pod until entries expire.",
            signatureOptions.Value.ReplayMemoryEntries);
    }

    /// <summary>
    /// Releases the in-memory cache.
    /// </summary>
    public void Dispose() => _seen.Dispose();
}
