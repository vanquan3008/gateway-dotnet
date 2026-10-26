using System.Security.Claims;
using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Authentication.ApiKey;
using Core.Gateway.Api.Headers;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Configuration;

public sealed class GatewayRoutePolicyOptionsTests
{
    [Theory]
    [InlineData("RateLimiting:Policies:bad name:PermitLimit", "10")]
    [InlineData("RateLimiting:Policies:strict:PermitLimit", "0")]
    [InlineData("RateLimiting:Policies:strict:WindowSeconds", "0")]
    public void InvalidRateLimitPolicies_AreRejected(string setting, string value)
    {
        var values = new Dictionary<string, string?> { ["RateLimiting:Policies:strict:PermitLimit"] = "10", [setting] = value };
        using var provider = Services().AddGatewayRateLimiting(Configuration(values)).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Correlation-ID")]
    [InlineData("X-Forwarded-For")]
    [InlineData("Bad Header")]
    public void ClaimsToHeaders_RejectsReservedOrInvalidHeaders(string header)
    {
        var configuration = Configuration(new() { [$"Headers:ClaimsToHeaders:{header}"] = "sub" });
        using var provider = Services().AddGatewayHeaders(configuration).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayHeaderOptions>>().Value);
    }

    [Theory]
    [InlineData("Resilience:TimeoutPolicies:api", "5")]
    [InlineData("Resilience:TimeoutPolicies:quick", "0")]
    [InlineData("Resilience:TimeoutPolicies:slow", "3601")]
    [InlineData("Resilience:RetryAttempts", "4")]
    public void InvalidResilienceRouteSettings_AreRejected(string setting, string value)
    {
        using var provider = Services().AddGatewayResilience(Configuration(new() { [setting] = value })).BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayResilienceOptions>>().Value);
    }

    [Theory]
    [InlineData("Authentication:ApiKeys:Clients:billing:Enabled", "false", "at least one enabled client")]
    [InlineData("Authentication:ApiKeys:Clients:billing:KeyHash", "not-a-hash", "64-character hex SHA-256")]
    [InlineData("Authentication:ApiKeys:Clients:copy:KeyHash", ValidHash, "unique across clients")]
    [InlineData("Authentication:ApiKeys:HeaderName", "Authorization", "custom header name")]
    [InlineData("Authentication:ApiKeys:Clients:billing:Scopes:0", "orders read", "without spaces")]
    public void InvalidApiKeySettings_AreRejected(string setting, string value, string error)
    {
        var configuration = Configuration(new()
        {
            ["Authentication:Enabled"] = "true",
            ["Authentication:Jwt:Authority"] = "https://identity.example.com",
            ["Authentication:Jwt:Audience"] = "gateway",
            ["Authentication:ApiKeys:Enabled"] = "true",
            ["Authentication:ApiKeys:Clients:billing:KeyHash"] = ValidHash,
            [setting] = value
        });
        using var provider = Services().AddGatewayAuthentication(configuration, isProduction: false).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GatewayApiKeyOptions>>().Value);
        Assert.Contains(error, exception.Message);
    }

    private const string ValidHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static IServiceCollection Services() => new ServiceCollection().AddLogging();

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
