using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.Redis;
using Core.Gateway.Api.Redis.RateLimiting;
using Core.Gateway.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Core.Gateway.Api.Tests.Redis.RateLimiting;

[Collection("Redis rate limiting")]
public sealed class RedisDistributedRateLimitStoreTests : IAsyncLifetime
{
    private sealed record Setup(GatewayRateLimitingOptions RateLimiting, RedisOptions Redis);

    private static Setup Settings(string? connection = null, string? prefix = null, int timeout = 500) => new(
        new GatewayRateLimitingOptions { Distributed = new() { Enabled = true } },
        new RedisOptions
        {
            ConnectionString = connection ?? Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS"),
            KeyPrefix = prefix ?? $"test:store:{Guid.NewGuid():N}",
            TimeoutMilliseconds = timeout,
            ConnectTimeoutMilliseconds = 500,
            FailureCooldownSeconds = 1
        });

    private readonly List<RedisConnection> _connections = [];

    private RedisDistributedRateLimitStore Create(Setup setup)
    {
        var redisOptions = Microsoft.Extensions.Options.Options.Create(setup.Redis);
        var connection = new RedisConnection(redisOptions);
        _connections.Add(connection);
        return new(Microsoft.Extensions.Options.Options.Create(setup.RateLimiting), redisOptions, connection,
            NullLogger<RedisDistributedRateLimitStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    [RedisFact]
    public async Task ConcurrentInstances_ConsumeExactlyTheSharedQuotaAndSetTtlWithoutRawIdentity()
    {
        var settings = Settings();
        var first = Create(settings);
        var second = Create(settings);
        await first.AcquireAsync("probe:startup", int.MaxValue, TimeSpan.FromSeconds(1), default);
        await second.AcquireAsync("probe:startup", int.MaxValue, TimeSpan.FromSeconds(1), default);
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            (i % 2 == 0 ? first : second).AcquireAsync("user:private-user@example.test", 25, TimeSpan.FromSeconds(60), default).AsTask()));
        Assert.All(results, result => Assert.NotEqual(DistributedRateLimitStatus.Unavailable, result.Status));
        Assert.Equal(25, results.Count(result => result.Status == DistributedRateLimitStatus.Allowed));
        Assert.Equal(75, results.Count(result => result.Status == DistributedRateLimitStatus.Rejected));
        await using var connection = await ConnectionMultiplexer.ConnectAsync(settings.Redis.ConnectionString!);
        var keys = connection.GetServer(connection.GetEndPoints().First()).Keys(pattern: settings.Redis.KeyPrefix + ":ratelimit:sw:user:*").ToArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.DoesNotContain("private-user", key.ToString());
            Assert.DoesNotContain("@example", key.ToString());
        });
        var database = connection.GetDatabase();
        Assert.Equal(25, keys.Sum(key => (long)database.StringGet(key)));
        foreach (var key in keys)
        {
            var ttl = await database.KeyTimeToLiveAsync(key);

            Assert.True(ttl > TimeSpan.Zero && ttl <= TimeSpan.FromSeconds(120));
        }
    }

    [RedisFact]
    public async Task ARejectedRequest_TellsHowLongToWaitAndIsAdmittedAfterThat()
    {
        var settings = Settings();
        var store = Create(settings);
        await store.AcquireAsync("probe:startup", int.MaxValue, TimeSpan.FromSeconds(1), default);
        var first = await store.AcquireAsync("ip:198.51.100.1", 1, TimeSpan.FromSeconds(1), default);
        var denied = await store.AcquireAsync("ip:198.51.100.1", 1, TimeSpan.FromSeconds(1), default);
        Assert.Equal(DistributedRateLimitStatus.Allowed, first.Status);
        Assert.Equal(DistributedRateLimitStatus.Rejected, denied.Status);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
        await Task.Delay(denied.RetryAfter + TimeSpan.FromMilliseconds(100));
        var renewed = await store.AcquireAsync("ip:198.51.100.1", 1, TimeSpan.FromSeconds(1), default);
        Assert.Equal(DistributedRateLimitStatus.Allowed, renewed.Status);
    }

    [RedisFact]
    public async Task RedisCommandFailure_ReturnsUnavailableAndRecoversAfterCooldown()
    {
        var adminSettings = Settings();
        await using var admin = await ConnectionMultiplexer.ConnectAsync(adminSettings.Redis.ConnectionString!);
        var server = admin.GetServer(admin.GetEndPoints().First());
        var name = $"gateway-test-{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await server.ExecuteAsync("ACL", "SETUSER", name, "on", ">" + password, "~*", "&*", "+@all");
        try
        {
            var configuration = ConfigurationOptions.Parse(adminSettings.Redis.ConnectionString!);
            configuration.User = name;
            configuration.Password = password;
            var settings = Settings(configuration.ToString(includePassword: true));
            var store = Create(settings);
            await store.AcquireAsync("probe:startup", int.MaxValue, TimeSpan.FromSeconds(1), default);
            var beforeFailure = await store.AcquireAsync("user:failure-test", 10, TimeSpan.FromMinutes(1), default);
            Assert.Equal(DistributedRateLimitStatus.Allowed, beforeFailure.Status);
            await server.ExecuteAsync("ACL", "SETUSER", name, "-incr");
            var unavailable = await store.AcquireAsync("user:failure-test", 10, TimeSpan.FromMinutes(1), default);
            Assert.Equal(DistributedRateLimitStatus.Unavailable, unavailable.Status);
            await server.ExecuteAsync("ACL", "SETUSER", name, "+incr");
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
            var store = Create(Settings(configuration.ToString(includePassword: true), timeout: 2_000));
            await store.AcquireAsync("probe:startup", int.MaxValue, TimeSpan.FromSeconds(1), default);
            await server.ExecuteAsync("ACL", "SETUSER", name, "-incr");
            Assert.Equal(DistributedRateLimitStatus.Unavailable,
                (await store.AcquireAsync("user:recovery", 100, TimeSpan.FromMinutes(1), default)).Status);
            await server.ExecuteAsync("ACL", "SETUSER", name, "+incr");
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
        var store = Create(Settings());
        await store.AcquireAsync("probe:startup", int.MaxValue, TimeSpan.FromSeconds(1), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.AcquireAsync("user:cancelled", 1, TimeSpan.FromMinutes(1), cancellation.Token).AsTask());
        var result = await store.AcquireAsync("user:cancelled", 1, TimeSpan.FromMinutes(1), default);
        Assert.Equal(DistributedRateLimitStatus.Allowed, result.Status);
    }
}
