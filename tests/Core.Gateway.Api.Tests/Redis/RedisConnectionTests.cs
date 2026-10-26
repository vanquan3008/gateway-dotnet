using Core.Gateway.Api.Redis;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Redis;

public sealed class RedisConnectionTests
{
    [Fact]
    public void IsConfigured_OnlyNeedsAConnectionString()
    {
        Assert.False(Create(null).IsConfigured);
        Assert.False(Create("  ").IsConfigured);
        Assert.True(Create("127.0.0.1:1").IsConfigured);
    }

    [Fact]
    public async Task AnUnreachableRedis_IsReportedAsOneKindOfFailure()
    {
        await using var redis = Create("127.0.0.1:1");

        var exception = await Assert.ThrowsAsync<RedisUnavailableException>(
            async () => await redis.ExecuteAsync(database => database.PingAsync(), CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(exception.Reason));
    }

    [Fact]
    public async Task WithoutAConnectionString_RedisIsReportedUnavailable()
    {
        await using var redis = Create(null);

        var exception = await Assert.ThrowsAsync<RedisUnavailableException>(
            async () => await redis.ExecuteAsync(database => database.PingAsync(), CancellationToken.None));

        Assert.Equal(nameof(InvalidOperationException), exception.Reason);
    }

    [Fact]
    public async Task Cancelling_IsNotReportedAsRedisBeingDown()
    {
        await using var redis = Create("127.0.0.1:1");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await redis.ExecuteAsync(database => database.PingAsync(), cancelled.Token));
    }

    [Fact]
    public async Task AfterDisposal_TheConnectionRefusesToBeUsed()
    {
        var redis = Create("127.0.0.1:1");
        await redis.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await redis.ExecuteAsync(database => database.PingAsync(), CancellationToken.None));
    }

    private static RedisConnection Create(string? connection) =>
        new(Options.Create(new RedisOptions
        {
            ConnectionString = connection,
            TimeoutMilliseconds = 200,
            ConnectTimeoutMilliseconds = 200
        }));
}
