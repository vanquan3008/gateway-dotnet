using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Options;
using Core.Gateway.Api.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests;

public sealed class GatewayConfigurationTests
{
    [Fact]
    public void RouteFile_DoesNotOverrideCommandLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-routes-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"ReverseProxy":{"Routes":{"test":{"ClusterId":"file-cluster"}}}}""");
        try
        {
            using var configuration = new ConfigurationManager();
            configuration["Gateway:RoutesFile"] = path;
            configuration.AddGatewayRoutes(["--ReverseProxy:Routes:test:ClusterId=cli-cluster"]);
            Assert.Equal("cli-cluster", configuration["ReverseProxy:Routes:test:ClusterId"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("RateLimiting:Local:Service:PermitLimit", "0")]
    [InlineData("RateLimiting:Local:InstanceConcurrencyLimit", "0")]
    [InlineData("RateLimiting:Local:ServiceConcurrencyLimit", "1025")]
    public void InvalidTrafficLimits_AreRejected(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [setting] = value
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value);
    }

    [Theory]
    [InlineData("Resilience:BackendHealthWindowSeconds", "10")]
    [InlineData("Resilience:ActivityTimeoutSeconds", "0")]
    [InlineData("Resilience:PooledConnectionLifetimeSeconds", "0")]
    public void InvalidResilienceSettings_AreRejected(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [setting] = value
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayResilience(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayResilienceOptions>>().Value);
    }
}
