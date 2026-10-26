using Core.Gateway.Api.Authentication.Signature;
using Core.Gateway.Api.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Configuration;

public sealed class GatewayRedisRequirementTests
{
    private const string RedisSetting = "Redis:ConnectionString";
    private const string RedisRule = "requires Redis:ConnectionString";

    [Fact]
    public void SharedQuotas_InProduction_NeedRedis()
    {
        using var provider = RateLimiting(production: true, redis: false);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value);
        Assert.Contains(RedisRule, exception.Message);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void SharedQuotas_AreAccepted_WhenRedisIsConfiguredOrProductionRulesAreOff(bool production, bool redis)
    {
        using var provider = RateLimiting(production, redis);

        Assert.True(provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value.Distributed.Enabled);
    }

    [Fact]
    public void LocalOnlyLimits_InProduction_DoNotNeedRedis()
    {
        var configuration = Configuration(new() { ["RateLimiting:Distributed:Enabled"] = "false" });
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayRateLimiting(configuration, requireProductionConfiguration: true)
            .BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value.Distributed.Enabled);
    }

    [Fact]
    public void ReplayRejection_InProduction_NeedsRedis()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => ReadSignatureOptions(production: true, redis: false, rejectReplays: true));

        Assert.Contains(RedisRule, exception.Message);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public void ReplayRejection_NeverFailsOnTheRedisRule_WhenItIsSatisfiedOrNotNeeded(bool production, bool redis, bool rejectReplays)
    {
        var exception = Record.Exception(() => ReadSignatureOptions(production, redis, rejectReplays));

        Assert.DoesNotContain(RedisRule, exception?.Message ?? string.Empty);
    }

    private static ServiceProvider RateLimiting(bool production, bool redis)
    {
        var values = new Dictionary<string, string?> { ["RateLimiting:Distributed:Enabled"] = "true" };
        if (redis)
        {
            values[RedisSetting] = "redis.example.com:6379";
        }

        return new ServiceCollection()
            .AddLogging()
            .AddGatewayRateLimiting(Configuration(values), production)
            .BuildServiceProvider();
    }

    private static void ReadSignatureOptions(bool production, bool redis, bool rejectReplays)
    {
        var values = new Dictionary<string, string?>
        {
            ["Authentication:Signature:Enabled"] = "true",
            ["Authentication:Signature:RejectReplays"] = rejectReplays.ToString()
        };
        if (redis)
        {
            values[RedisSetting] = "redis.example.com:6379";
        }

        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication().AddGatewaySignature(Configuration(values), production);
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<GatewaySignatureOptions>>().Value;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
