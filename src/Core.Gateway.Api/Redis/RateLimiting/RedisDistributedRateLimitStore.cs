
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Redis.RateLimiting;

/// <summary>
/// Redis implementation of the shared quota store, with a circuit breaker.
/// </summary>
public sealed class RedisDistributedRateLimitStore(
    IOptions<GatewayRateLimitingOptions> options,
    IOptions<RedisOptions> redisOptions,
    IRedisConnection redis,
    ILogger<RedisDistributedRateLimitStore> logger)
    : IDistributedRateLimitStore
{
    private const long MillisecondsPerSecond = 1_000;
    private readonly GatewayRateLimitingOptions _settings = options.Value;
    private readonly RedisOptions _redisSettings = redisOptions.Value;
    private readonly SlidingWindowRateLimiter _limiter = new(
        new RedisRateLimitCounterStorage(redis, $"{redisOptions.Value.KeyPrefix}:ratelimit"),
        TimeProvider.System);
    private readonly object _availabilityLock = new();
    private long _retryAtMilliseconds;
    private long _failureVersion;
    private bool _isUnavailable;
    private bool _recoveryProbeActive;

    public bool IsConfigured =>
        _settings.Distributed.Enabled &&
        redis.IsConfigured;

    private bool IsDistributedEnabled =>
        _settings.Enabled &&
        IsConfigured;

    /// <summary>
    /// Takes a permit from a shared quota in Redis behind a circuit breaker; reports Unavailable while Redis is down.
    /// </summary>
    /// <param name="partitionKey">Identifies the quota.</param>
    /// <param name="permitLimit">Requests allowed per window.</param>
    /// <param name="window">Window length.</param>
    /// <param name="cancellationToken">Cancels the Redis call.</param>
    /// <returns>Allowed or Rejected, or Unavailable when Redis cannot be used.</returns>
    /// <exception cref="ArgumentException">The partition key is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit or window is not positive.</exception>
    public async ValueTask<DistributedRateLimitResult> AcquireAsync(
        string partitionKey,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDistributedEnabled)
        {
            return DistributedRateLimitResult.Unavailable();
        }

        if (!TryBeginOperation(out long failureVersion, out bool isRecoveryProbe, out TimeSpan retryAfter))
        {
            return DistributedRateLimitResult.Unavailable(retryAfter);
        }

        try
        {
            DistributedRateLimitResult result =
                await _limiter.AcquireAsync(partitionKey, permitLimit, window, cancellationToken);

            MarkAvailableAfterSuccess(failureVersion);

            return result;
        }
        catch (RedisUnavailableException exception)
        {
            MarkUnavailable(exception);

            return DistributedRateLimitResult.Unavailable(
                TimeSpan.FromSeconds(_redisSettings.FailureCooldownSeconds));
        }
        finally
        {
            if (isRecoveryProbe)
            {
                EndRecoveryProbe();
            }
        }
    }

    /// <summary>
    /// Circuit breaker gate: refuses calls during the cooldown, then lets one recovery probe through.
    /// </summary>
    /// <param name="failureVersion">Failure generation at the start of the call.</param>
    /// <param name="isRecoveryProbe">True when this call is the probe.</param>
    /// <param name="retryAfter">Time until the next attempt when refused.</param>
    /// <returns>True when the call may go to Redis.</returns>
    private bool TryBeginOperation(
        out long failureVersion,
        out bool isRecoveryProbe,
        out TimeSpan retryAfter)
    {
        lock (_availabilityLock)
        {
            failureVersion = _failureVersion;
            isRecoveryProbe = false;
            retryAfter = TimeSpan.Zero;
            if (!_isUnavailable)
            {
                return true;
            }
            var remaining = _retryAtMilliseconds - Environment.TickCount64;
            if (remaining > 0)
            {
                retryAfter = TimeSpan.FromMilliseconds(remaining);
                return false;
            }
            if (_recoveryProbeActive)
            {
                retryAfter = TimeSpan.FromSeconds(1);
                return false;
            }

            _recoveryProbeActive = true;
            isRecoveryProbe = true;

            return true;
        }
    }

    /// <summary>
    /// Closes the breaker after a successful call.
    /// </summary>
    /// <param name="failureVersion">Failure generation the call started under.</param>
    private void MarkAvailableAfterSuccess(long failureVersion)
    {
        lock (_availabilityLock)
        {
            if (!_isUnavailable ||
                failureVersion != _failureVersion)
            {
                return;
            }
            _isUnavailable = false;
            _retryAtMilliseconds = 0;
        }

        logger.LogInformation(
            "Redis distributed rate limiting recovered; shared quotas are active.");
    }

    /// <summary>
    /// Opens the breaker and starts the cooldown.
    /// </summary>
    /// <param name="exception">The failure that opened it.</param>
    private void MarkUnavailable(RedisUnavailableException exception)
    {
        bool shouldLog;

        lock (_availabilityLock)
        {
            shouldLog = !_isUnavailable;
            _failureVersion++;
            _isUnavailable = true;
            long cooldownMilliseconds =
                Math.Max(1, _redisSettings.FailureCooldownSeconds) *
                MillisecondsPerSecond;

            _retryAtMilliseconds =
                Environment.TickCount64 + cooldownMilliseconds;
        }

        if (!shouldLog)
        {
            return;
        }

        logger.LogWarning(
            "Redis distributed rate limiting unavailable ({Reason}); shared quotas cannot be enforced.",
            exception.Reason);
    }

    /// <summary>
    /// Marks the recovery probe as finished so the next one can run.
    /// </summary>
    private void EndRecoveryProbe()
    {
        lock (_availabilityLock)
        {
            _recoveryProbeActive = false;
        }
    }
}
