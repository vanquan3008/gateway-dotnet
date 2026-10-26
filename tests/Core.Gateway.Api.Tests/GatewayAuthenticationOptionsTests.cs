using Core.Gateway.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests;

public sealed class GatewayAuthenticationOptionsTests
{
    [Fact]
    public async Task AddGatewayAuthentication_RequiresIdentityWhenEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true",
                ["Authentication:Authority"] = "https://identity.example.com",
                ["Authentication:Audience"] = "gateway"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: false)
            .BuildServiceProvider();

        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallbackPolicy = await policyProvider.GetFallbackPolicyAsync();

        Assert.NotNull(fallbackPolicy);
        Assert.Contains(
            fallbackPolicy.Requirements,
            requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void AddGatewayAuthentication_RejectsDisabledProductionAuthentication()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "false"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: true)
            .BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GatewayAuthenticationOptions>>().Value);

        Assert.Contains("enabled in Production", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("30001")]
    public void AddGatewayAuthentication_RejectsInvalidBackchannelTimeout(string timeout)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:BackchannelTimeoutMilliseconds"] = timeout
        }).Build();
        using var provider = new ServiceCollection().AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: false).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayAuthenticationOptions>>().Value);
    }
}
