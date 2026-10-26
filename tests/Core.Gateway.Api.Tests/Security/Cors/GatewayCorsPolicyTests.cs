using Core.Gateway.Api.Headers;
using Core.Gateway.Api.Security.Cors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Security.Cors;

public sealed class GatewayCorsPolicyTests
{
    [Fact]
    public void Browsers_CanReadCorrelationAndRateLimitHeaders()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:Enabled"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://app.example.com"
            })
            .Build();
        using var provider = new ServiceCollection().AddGatewayCors(configuration).BuildServiceProvider();

        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(GatewayCorsExtensions.PolicyName)!;

        Assert.Contains(GatewayHeaders.CorrelationId, policy.ExposedHeaders);
        Assert.Contains("Retry-After", policy.ExposedHeaders);
        Assert.Contains("X-RateLimit-Limit", policy.ExposedHeaders);
        Assert.Contains("X-RateLimit-Remaining", policy.ExposedHeaders);
        Assert.Contains("X-RateLimit-Reset", policy.ExposedHeaders);
    }
}
