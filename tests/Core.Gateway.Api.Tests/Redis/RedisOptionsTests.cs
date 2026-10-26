using Core.Gateway.Api.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Redis;

public sealed class RedisOptionsTests
{
    [Fact]
    public void Defaults_NeedNoRedisAtAll()
    {
        var options = Resolve(new Dictionary<string, string?>());

        Assert.False(options.IsConfigured);
        Assert.Equal("gateway", options.KeyPrefix);
        Assert.Equal(250, options.TimeoutMilliseconds);
        Assert.Equal(2_000, options.ConnectTimeoutMilliseconds);
        Assert.Equal(5, options.FailureCooldownSeconds);
    }

    [Fact]
    public void AreBoundFromTheirOwnSection_NotFromRateLimiting()
    {
        var options = Resolve(new Dictionary<string, string?>
        {
            ["Redis:ConnectionString"] = "redis.internal:6379",
            ["Redis:KeyPrefix"] = "core-gateway:production",
            ["Redis:TimeoutMilliseconds"] = "400",
            ["Redis:FailureCooldownSeconds"] = "10",
            ["RateLimiting:Distributed:RedisConnectionString"] = "ignored:1"
        });

        Assert.True(options.IsConfigured);
        Assert.Equal("redis.internal:6379", options.ConnectionString);
        Assert.Equal("core-gateway:production", options.KeyPrefix);
        Assert.Equal(400, options.TimeoutMilliseconds);
        Assert.Equal(10, options.FailureCooldownSeconds);
    }

    [Theory]
    [InlineData("KeyPrefix", "bad{prefix}")]
    [InlineData("KeyPrefix", "")]
    [InlineData("TimeoutMilliseconds", "0")]
    [InlineData("ConnectTimeoutMilliseconds", "10")]
    [InlineData("FailureCooldownSeconds", "0")]
    public void InvalidSettings_AreRejected(string setting, string value)
    {
        Assert.Throws<OptionsValidationException>(() => Resolve(new() { [$"Redis:{setting}"] = value }));
    }

    [Theory]
    [InlineData("localhost:6379,asyncTimeout=invalid")]
    [InlineData("localhost:6379,unsupportedSetting=true")]
    public void AMalformedConnectionString_IsRejectedWithoutEchoingIt(string connection)
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(new() { ["Redis:ConnectionString"] = connection }));

        Assert.DoesNotContain(connection, exception.Message);
    }

    [Fact]
    public void AddGatewayRedis_CanBeCalledByEveryFeatureAndStillRegistersOnce()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddLogging();

        services.AddGatewayRedis(configuration).AddGatewayRedis(configuration).AddGatewayRedis(configuration);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IRedisConnection));
    }

    private static RedisOptions Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRedis(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RedisOptions>>().Value;
    }
}
