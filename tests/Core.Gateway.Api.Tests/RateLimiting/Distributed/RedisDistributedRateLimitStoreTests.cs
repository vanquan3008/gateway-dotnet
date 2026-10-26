using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Options;
using Core.Gateway.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting.Distributed;

[Collection("Redis rate limiting")]
public sealed class RedisDistributedRateLimitStoreTests
{
    private static GatewayRateLimitingOptions Settings(string? connection = null, string? prefix = null, int timeout = 500) => new()
    {
        Distributed = new()
        {
            Enabled = true,
            RedisConnectionString = connection ?? Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS"),
            KeyPrefix = prefix ?? $"test:store:{Guid.NewGuid():N}",
            TimeoutMilliseconds = timeout,
            ConnectTimeoutMilliseconds = 500,
            FailureCooldownSeconds = 1
        }
    };

    private static RedisDistributedRateLimitStore Create(GatewayRateLimitingOptions settings) =>
        new(Microsoft.Extensions.Options.Options.Create(settings), NullLogger<RedisDistributedRateLimitStore>.Instance);

    [RedisFact]
    public async Task ConcurrentInstances_ConsumeExactlyTheSharedQuotaAndSetTtlWithoutRawIdentity()
    {
        var settings = Settings();
        await using var first = Create(settings);
        await using var second = Create(settings);
        await first.StartAsync(default);
        await second.StartAsync(default);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            (i % 2 == 0 ? first : second).AcquireAsync("user:private-user@example.test", 25, TimeSpan.FromSeconds(60), default).AsTask()));
        Assert.All(results, result => Assert.NotEqual(DistributedRateLimitStatus.Unavailable, result.Status));
        Assert.Equal(25, results.Count(result => result.Status == DistributedRateLimitStatus.Allowed));
        Assert.Equal(75, results.Count(result => result.Status == DistributedRateLimitStatus.Rejected));
        await using var connection = await ConnectionMultiplexer.ConnectAsync(settings.Distributed.RedisConnectionString!);
        var key = connection.GetServer(connection.GetEndPoints().First()).Keys(pattern: settings.Distributed.KeyPrefix + ":user:*").Single();
        Assert.DoesNotContain("private-user", key.ToString());
        Assert.DoesNotContain("@example", key.ToString());
        Assert.Equal("25", (await connection.GetDatabase().StringGetAsync(key)).ToString());
        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.True(ttl > TimeSpan.Zero && ttl <= TimeSpan.FromSeconds(60));
    }

    [RedisFact]
    public async Task WindowExpiry_ReplenishesWithoutExtendingTtlOnRejection()
    {
        var settings = Settings();
        await using var store = Create(settings);
        await store.StartAsync(default);
        var first = await store.AcquireAsync("ip:198.51.100.1", 1, TimeSpan.FromSeconds(1), default);
        await Task.Delay(100);
        var denied = await store.AcquireAsync("ip:198.51.100.1", 1, TimeSpan.FromSeconds(1), default);
        Assert.Equal(DistributedRateLimitStatus.Allowed, first.Status);
        Assert.Equal(DistributedRateLimitStatus.Rejected, denied.Status);
        Assert.True(denied.RetryAfter < first.RetryAfter);
        await Task.Delay(denied.RetryAfter + TimeSpan.FromMilliseconds(100));
        var renewed = await store.AcquireAsync("ip:198.51.100.1", 1, TimeSpan.FromSeconds(1), default);
        Assert.Equal(DistributedRateLimitStatus.Allowed, renewed.Status);
    }

    [RedisFact]
    public async Task RedisScriptFailure_ReturnsUnavailableAndRecoversAfterCooldown()
    {
        var adminSettings = Settings();
        await using var admin = await ConnectionMultiplexer.ConnectAsync(adminSettings.Distributed.RedisConnectionString!);
        var server = admin.GetServer(admin.GetEndPoints().First());
        var name = $"gateway-test-{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await server.ExecuteAsync("ACL", "SETUSER", name, "on", ">" + password, "~*", "&*", "+@all");
        try
        {
            var configuration = ConfigurationOptions.Parse(adminSettings.Distributed.RedisConnectionString!);
            configuration.User = name;
            configuration.Password = password;
            var settings = Settings(configuration.ToString(includePassword: true));
            await using var store = Create(settings);
            await store.StartAsync(default);
            var beforeFailure = await store.AcquireAsync("user:failure-test", 10, TimeSpan.FromMinutes(1), default);
            Assert.Equal(DistributedRateLimitStatus.Allowed, beforeFailure.Status);
            await server.ExecuteAsync("ACL", "SETUSER", name, "-eval", "-evalsha");
            var unavailable = await store.AcquireAsync("user:failure-test", 10, TimeSpan.FromMinutes(1), default);
            Assert.Equal(DistributedRateLimitStatus.Unavailable, unavailable.Status);
            await server.ExecuteAsync("ACL", "SETUSER", name, "+eval", "+evalsha");
            var duringCooldown = await store.AcquireAsync("user:failure-test", 10, TimeSpan.FromMinutes(1), default);
            Assert.Equal(DistributedRateLimitStatus.Unavailable, duringCooldown.Status);
            await Task.Delay(1_100);
            var recovered = await store.AcquireAsync("user:failure-test", 10, TimeSpan.FromMinutes(1), default);
            Assert.Equal(DistributedRateLimitStatus.Allowed, recovered.Status);
        }
        finally { await server.ExecuteAsync("ACL", "DELUSER", name); }
    }

    [RedisFact]
    public async Task RecoveryAfterCooldown_AllowsOnlyOnePendingProbe()
    {
        var adminConfiguration = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS")!);
        adminConfiguration.AllowAdmin = true;
        await using var admin = await ConnectionMultiplexer.ConnectAsync(adminConfiguration);
        var server = admin.GetServer(admin.GetEndPoints().First());
        var name = $"gateway-test-{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await server.ExecuteAsync("ACL", "SETUSER", name, "on", ">" + password, "~*", "&*", "+@all");
        try
        {
            var configuration = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS")!);
            configuration.User = name;
            configuration.Password = password;
            await using var store = Create(Settings(configuration.ToString(includePassword: true), timeout: 2_000));
            await store.StartAsync(default);
            await server.ExecuteAsync("ACL", "SETUSER", name, "-eval", "-evalsha");
            Assert.Equal(DistributedRateLimitStatus.Unavailable,
                (await store.AcquireAsync("user:recovery", 100, TimeSpan.FromMinutes(1), default)).Status);
            await server.ExecuteAsync("ACL", "SETUSER", name, "+eval", "+evalsha");
            await Task.Delay(1_100);
            await server.ExecuteAsync("CLIENT", "PAUSE", "500", "WRITE");
            var attempts = Enumerable.Range(0, 100).Select(_ =>
                store.AcquireAsync("user:recovery", 100, TimeSpan.FromMinutes(1), default).AsTask()).ToArray();
            Assert.Equal(99, attempts.Count(task => task.IsCompletedSuccessfully &&
                task.Result.Status == DistributedRateLimitStatus.Unavailable));
            var results = await Task.WhenAll(attempts);
            Assert.Equal(1, results.Count(result => result.Status == DistributedRateLimitStatus.Allowed));
            Assert.Equal(DistributedRateLimitStatus.Allowed,
                (await store.AcquireAsync("user:recovery", 100, TimeSpan.FromMinutes(1), default)).Status);
        }
        finally { await server.ExecuteAsync("ACL", "DELUSER", name); }
    }

    [RedisFact]
    public async Task CancelledAcquisition_DoesNotConsumeQuota()
    {
        await using var store = Create(Settings());
        await store.StartAsync(default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.AcquireAsync("user:cancelled", 1, TimeSpan.FromMinutes(1), cancellation.Token).AsTask());
        var result = await store.AcquireAsync("user:cancelled", 1, TimeSpan.FromMinutes(1), default);
        Assert.Equal(DistributedRateLimitStatus.Allowed, result.Status);
    }
}
