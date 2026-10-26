using Core.Gateway.Api.Configuration.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests;

public sealed class GatewaySecurityOptionsTests
{
    [Theory]
    [InlineData("AllowedHosts", "*")]
    [InlineData("AllowedHosts", "0.0.0.0")]
    [InlineData("AllowedHosts", "[::]")]
    [InlineData("AllowedHosts", "::")]
    [InlineData("AllowedForwardedHosts", "*")]
    [InlineData("AllowedForwardedHosts", "0.0.0.0")]
    [InlineData("AllowedForwardedHosts", "[::]")]
    [InlineData("AllowedForwardedHosts", "::")]
    public void AddGatewaySecurity_RejectsUnrestrictedProductionHosts(string setting, string host)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GatewaySecurity:AllowedHosts:0"] = "api.example.com",
            ["GatewaySecurity:AllowedForwardedHosts:0"] = "api.example.com",
            ["GatewaySecurity:KnownProxies:0"] = "127.0.0.1",
            [$"GatewaySecurity:{setting}:0"] = host
        }).Build();
        using var provider = new ServiceCollection().AddGatewaySecurity(configuration, requireProductionConfiguration: true).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value);
        Assert.Contains($"GatewaySecurity:{setting}", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void AddGatewaySecurity_RejectsTrustingEntireInternet(string network)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GatewaySecurity:AllowedHosts:0"] = "api.example.com",
            ["GatewaySecurity:AllowedForwardedHosts:0"] = "api.example.com",
            ["GatewaySecurity:KnownNetworks:0"] = network
        }).Build();
        using var provider = new ServiceCollection().AddGatewaySecurity(configuration, requireProductionConfiguration: true).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value);
    }

    [Theory]
    [InlineData("KnownNetworks", "192.0.2.0/24")]
    [InlineData("KnownNetworks", "2001:db8::/48")]
    [InlineData("KnownProxies", "203.0.113.5")]
    public void AddGatewaySecurity_RejectsSampleIngressRangesInProduction(string setting, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GatewaySecurity:AllowedHosts:0"] = "api.example.com",
            ["GatewaySecurity:AllowedForwardedHosts:0"] = "api.example.com",
            [$"GatewaySecurity:{setting}:0"] = value
        }).Build();
        using var provider = new ServiceCollection().AddGatewaySecurity(configuration, requireProductionConfiguration: true).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value);
        Assert.Contains("documentation sample range", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddGatewaySecurity_RejectsUnsafeProductionDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GatewaySecurity:AllowedHosts:0"] = "*"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddGatewaySecurity(configuration, requireProductionConfiguration: true)
            .BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value);

        Assert.Contains("KnownProxies or KnownNetworks", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AllowedHosts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddGatewaySecurity_AcceptsRestrictedProductionConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GatewaySecurity:AllowedHosts:0"] = "api.example.com",
                ["GatewaySecurity:KnownNetworks:0"] = "10.0.0.0/24",
                ["GatewaySecurity:AllowedForwardedHosts:0"] = "api.example.com"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddGatewaySecurity(configuration, requireProductionConfiguration: true)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value;

        Assert.Equal("10.0.0.0/24", options.KnownNetworks.Single());
    }
}
