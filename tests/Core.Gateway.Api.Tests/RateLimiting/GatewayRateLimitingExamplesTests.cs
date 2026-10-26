using Core.Gateway.Api.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting;

public sealed class GatewayRateLimitingExamplesTests
{
    private static GatewayRateLimitingOptions Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "examples", "rate-limiting", file);
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();

        return provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value;
    }

    [Theory]
    [InlineData("standard.json")]
    [InlineData("tenant-api-key-and-routes.json")]
    [InlineData("local-only.json")]
    public void Example_PassesStartupValidation(string file) => Assert.NotNull(Load(file));

    [Fact]
    public void Standard_UsesSharedQuotaAndLocalFallback()
    {
        var options = Load("standard.json");

        Assert.Equal(300, options.Local.Anonymous!.PermitLimit);
        Assert.True(options.Distributed.Enabled);
        Assert.True(options.Distributed.Fallback.AutoScaleRatio);
        Assert.Equal(0.34, options.Distributed.Fallback.LocalRatio);
        Assert.True(options.Local.Anonymous.PermitLimit > options.Distributed.Anonymous.PermitLimit,
            "the local anonymous cap must stay above the shared quota or it would pre-empt it");
    }

    [Fact]
    public void TenantApiKeyAndRoutes_ShowsEveryOptionalQuota()
    {
        var options = Load("tenant-api-key-and-routes.json");

        Assert.NotNull(options.Distributed.Tenant);
        Assert.NotNull(options.Distributed.ApiKey);
        Assert.Equal(3, options.Policies.Count);
    }

    [Fact]
    public void LocalOnly_DisablesTheSharedStore()
    {
        Assert.False(Load("local-only.json").Distributed.Enabled);
    }
}
