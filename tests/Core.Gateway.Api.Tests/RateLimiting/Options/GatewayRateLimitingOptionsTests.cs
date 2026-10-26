using System.Net;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting.Options;

public sealed class GatewayRateLimitingOptionsTests
{
    [Fact]
    public void LocalLimiter_IsDisposedWithServiceProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        try
        {
            var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
            var context = new DefaultHttpContext();
            using (var lease = limiter.AttemptAcquire(context)) Assert.True(lease.IsAcquired);
            provider.Dispose();
            Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire(context));
        }
        finally { provider.Dispose(); }
    }

    [Fact]
    public void AddGatewayRateLimiting_BindsPerInstanceLimit()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Local:Instance:PermitLimit"] = "42"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayRateLimiting(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value;

        Assert.Equal(42, options.Local.Instance.PermitLimit);
    }

    [Fact]
    public void AddGatewayRateLimiting_AppliesAnonymousLimitPerIp()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Local:Anonymous:PermitLimit"] = "1"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayRateLimiting(configuration)
            .BuildServiceProvider();
        using var limiter = provider
            .GetRequiredService<IOptions<RateLimiterOptions>>()
            .Value
            .GlobalLimiter!;
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        using var firstLease = limiter.AttemptAcquire(context);
        using var secondLease = limiter.AttemptAcquire(context);

        Assert.True(firstLease.IsAcquired);
        Assert.False(secondLease.IsAcquired);
    }

    [Theory]
    [InlineData("Local:Anonymous:PermitLimit", "0")]
    [InlineData("Local:Service:QueueLimit", "-1")]
    [InlineData("Local:Instance:WindowSeconds", "86401")]
    [InlineData("Distributed:Authenticated:WindowSeconds", "0")]
    [InlineData("Distributed:Anonymous:QueueLimit", "1")]
    [InlineData("Distributed:Tenant:PermitLimit", "0")]
    [InlineData("Distributed:ApiKey:PermitLimit", "0")]
    [InlineData("Distributed:KeyPrefix", "bad{prefix}")]
    [InlineData("Distributed:TimeoutMilliseconds", "0")]
    [InlineData("Distributed:FailureCooldownSeconds", "0")]
    [InlineData("Distributed:UserIdClaim", "")]
    [InlineData("Distributed:TenantIdClaim", "")]
    [InlineData("Distributed:ApiKeyIdClaim", "")]
    public void AddGatewayRateLimiting_ValidatesNestedOptions(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"RateLimiting:{setting}"] = value
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value);
    }

    [Theory]
    [InlineData("Instance:PermitLimit")]
    [InlineData("Authenticated:PermitLimit")]
    [InlineData("Tenant:PermitLimit")]
    [InlineData("Anonymous:PermitLimit")]
    [InlineData("ServiceConcurrencyLimit")]
    public void LegacyConfiguration_FailsInsteadOfSilentlyUsingDifferentBudgets(string setting)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"RateLimiting:{setting}"] = "42"
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value);
        Assert.Contains("must be moved into Local or Distributed", exception.Message);
    }

    [Fact]
    public async Task AddGatewayRateLimiting_ValidatesOnHostStartup()
    {
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, builder) => builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Distributed:Authenticated:PermitLimit"] = "0"
            }))
            .ConfigureServices((context, services) => services.AddGatewayRateLimiting(context.Configuration))
            .Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData("localhost:6379,asyncTimeout=invalid")]
    [InlineData("localhost:6379,unsupportedSetting=true")]
    public void AddGatewayRateLimiting_RejectsMalformedRedisConfigurationWithoutEchoingIt(string connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Distributed:Enabled"] = "true",
            ["RateLimiting:Distributed:RedisConnectionString"] = connection
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value);
        Assert.DoesNotContain(connection, exception.Message);
    }
}
