using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Core.Gateway.Api.Redis.RateLimiting;

/// <summary>
/// Publishes this pod's heartbeat to Redis and counts live instances
/// to scale local fallback quotas.
/// </summary>
internal sealed class RedisInstanceHeartbeatService(
    IOptions<GatewayRateLimitingOptions> options,
    IOptions<RedisOptions> redisOptions,
    IRedisConnection redis,
    ActiveInstanceCounter instances,
    TimeProvider time,
    ILogger<RedisInstanceHeartbeatService> logger)
    : BackgroundService
{
    private const int MissedBeatsBeforeExpiry = 3;

    private readonly GatewayRateLimitingOptions _settings = options.Value;
    private readonly RedisKey _key =
        $"{redisOptions.Value.KeyPrefix}:ratelimit:instances";
    private readonly string _instanceId =
        $"{Environment.MachineName}:{Guid.NewGuid():N}";

    private bool _failing;

    private TimeSpan Interval =>
        TimeSpan.FromSeconds(_settings.Distributed.Fallback.HeartbeatSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);

        try
        {
            do
            {
                await BeatAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Gateway pod heartbeat stopped.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (!IsEnabled())
        {
            return;
        }

        try
        {
            await redis.ExecuteAsync(
                database => database.SortedSetRemoveAsync(_key, _instanceId),
                cancellationToken);
        }
        catch (RedisUnavailableException exception)
        {
            LogRemovalFailure(exception.Reason);
        }
        catch (OperationCanceledException)
        {
            LogRemovalFailure();
        }
    }

    private bool IsEnabled() =>
        _settings.Enabled &&
        _settings.Distributed.Enabled &&
        _settings.Distributed.Fallback.AutoScaleRatio &&
        redis.IsConfigured;

    private async Task BeatAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var ttl = Interval * MissedBeatsBeforeExpiry;

        try
        {
            var count = await redis.ExecuteAsync(
                async database =>
                {
                    var batch = database.CreateBatch();

                    var addTask = batch.SortedSetAddAsync(
                        _key,
                        _instanceId,
                        now);

                    var removeStaleTask = batch.SortedSetRemoveRangeByScoreAsync(
                        _key,
                        double.NegativeInfinity,
                        now - ttl.TotalMilliseconds);

                    var countTask = batch.SortedSetLengthAsync(_key);
                    var expireTask = batch.KeyExpireAsync(_key, ttl * 2);

                    batch.Execute();

                    await Task.WhenAll(
                        addTask,
                        removeStaleTask,
                        countTask,
                        expireTask);

                    return await countTask;
                },
                cancellationToken);

            instances.Set((int)Math.Min(count, int.MaxValue));
            LogRecoveryIfNeeded();
        }
        catch (RedisUnavailableException exception)
        {
            GatewayRateLimitingMetrics.RecordHeartbeatFailed();

            var level = _failing ? LogLevel.Debug : LogLevel.Warning;
            _failing = true;

            logger.Log(
                level,
                "Gateway pod heartbeat skipped ({Reason}); keeping {Count} known pods.",
                exception.Reason,
                instances.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Gateway pod heartbeat interrupted by shutdown.");
        }
    }

    private void LogRecoveryIfNeeded()
    {
        if (!_failing)
        {
            return;
        }

        _failing = false;

        logger.LogInformation(
            "Gateway pod heartbeat recovered; {Count} pods counted.",
            instances.Count);
    }

    private void LogRemovalFailure(string? reason = null)
    {
        logger.LogWarning(
            "Could not remove this pod from the heartbeat set ({Reason}); " +
            "other pods stop counting it after {Seconds}s.",
            reason ?? "shutdown cancelled",
            (Interval * MissedBeatsBeforeExpiry).TotalSeconds);
    }
}
