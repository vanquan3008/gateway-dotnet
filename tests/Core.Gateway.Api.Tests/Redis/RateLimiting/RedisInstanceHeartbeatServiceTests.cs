using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.Redis;
using Core.Gateway.Api.Redis.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Core.Gateway.Api.Tests.Redis.RateLimiting;

public sealed class RedisInstanceHeartbeatServiceTests
{
    [Fact]
    public async Task Stopping_WithoutRedis_DoesNotFail()
    {
        var redisOptions = Options.Create(new RedisOptions());
        await using var redis = new RedisConnection(redisOptions);
        var service = Create(EnabledOptions(), redisOptions, redis);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_WhileTheHeartbeatIsOff_LeavesRedisAlone()
    {
        var redis = new CountingRedis(configured: true);
        var service = Create(new GatewayRateLimitingOptions(), RedisOptions(), redis);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, redis.Calls);
    }

    [Fact]
    public async Task Stopping_WhileTheHeartbeatIsOn_RemovesThePodAndToleratesRedisBeingDown()
    {
        var redis = new CountingRedis(configured: true);
        var service = Create(EnabledOptions(), RedisOptions(), redis);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, redis.Calls);
    }

    private static RedisInstanceHeartbeatService Create(
        GatewayRateLimitingOptions options, IOptions<RedisOptions> redisOptions, IRedisConnection redis) =>
        new(Options.Create(options), redisOptions, redis, new ActiveInstanceCounter(), TimeProvider.System,
            NullLogger<RedisInstanceHeartbeatService>.Instance);

    private static GatewayRateLimitingOptions EnabledOptions() =>
        new() { Distributed = new DistributedRateLimitingOptions { Enabled = true } };

    private static IOptions<RedisOptions> RedisOptions() =>
        Options.Create(new RedisOptions { ConnectionString = "127.0.0.1:1" });

    private sealed class CountingRedis(bool configured) : IRedisConnection
    {
        public int Calls { get; private set; }

        public bool IsConfigured => configured;

        public ValueTask<T> ExecuteAsync<T>(Func<IDatabase, Task<T>> command, CancellationToken cancellationToken)
        {
            Calls++;
            throw new RedisUnavailableException(new TimeoutException());
        }
    }
}
