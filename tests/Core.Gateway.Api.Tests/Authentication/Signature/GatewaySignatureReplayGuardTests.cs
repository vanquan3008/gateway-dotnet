using Core.Gateway.Api.Authentication.Signature;
using Core.Gateway.Api.Authentication.Signature.Replay;
using Core.Gateway.Api.Redis;
using Core.Gateway.Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Authentication.Signature;

[Collection("Redis rate limiting")]
public sealed class GatewaySignatureReplayGuardTests : IAsyncLifetime
{
    [Fact]
    public async Task ASignature_IsRegisteredOnceAndRefusedAfterwards()
    {
        using var guard = CreateGuard();

        Assert.True(await guard.TryRegisterAsync("partner-a", "signature-1", CancellationToken.None));
        Assert.False(await guard.TryRegisterAsync("partner-a", "signature-1", CancellationToken.None));
        Assert.True(await guard.TryRegisterAsync("partner-a", "signature-2", CancellationToken.None));
    }

    [Fact]
    public async Task TheSameSignatureFromAnotherClient_IsNotAReplay()
    {
        using var guard = CreateGuard();

        Assert.True(await guard.TryRegisterAsync("partner-a", "signature-1", CancellationToken.None));
        Assert.True(await guard.TryRegisterAsync("partner-b", "signature-1", CancellationToken.None));
    }

    [Fact]
    public async Task WhenRedisIsDown_ThePodStillRemembersItsOwnSignatures()
    {
        using var guard = CreateGuard(redis: "127.0.0.1:1");

        Assert.True(await guard.TryRegisterAsync("partner-a", "signature-1", CancellationToken.None));
        Assert.False(await guard.TryRegisterAsync("partner-a", "signature-1", CancellationToken.None));
    }

    [RedisFact]
    public async Task WithRedis_AllPodsShareWhatHasBeenAccepted()
    {
        var redis = Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS")!;
        var prefix = $"test:replay:{Guid.NewGuid():N}";
        using var podA = CreateGuard(redis, prefix);
        using var podB = CreateGuard(redis, prefix);

        Assert.True(await podA.TryRegisterAsync("partner-a", "signature-1", CancellationToken.None));
        Assert.False(await podB.TryRegisterAsync("partner-a", "signature-1", CancellationToken.None));
    }

    private readonly List<RedisConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    private GatewaySignatureReplayGuard CreateGuard(string? redis = null, string prefix = "test:replay")
    {
        var redisOptions = Options.Create(new RedisOptions
        {
            ConnectionString = redis,
            KeyPrefix = prefix,
            TimeoutMilliseconds = 200,
            ConnectTimeoutMilliseconds = 200
        });
        var connection = new RedisConnection(redisOptions);
        _connections.Add(connection);

        return new(
            Options.Create(new GatewaySignatureOptions { MaxClockSkewSeconds = 60 }),
            redisOptions,
            connection,
            NullLogger<GatewaySignatureReplayGuard>.Instance);
    }
}
