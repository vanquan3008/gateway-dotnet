using System.Net;
using System.Security.Claims;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Local;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting;

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
    public async Task AnonymousLimit_AppliesPerIpToCallersWithoutAnIdentity()
    {
        using var provider = AnonymousLimitProvider();
        var middleware = provider.GetRequiredService<GatewayAnonymousRateLimitingMiddleware>();
        var reached = 0;

        var first = AnonymousRequest(provider, IPAddress.Loopback, junkHeader: "X-Api-Key");
        var second = AnonymousRequest(provider, IPAddress.Loopback, junkHeader: "Authorization");
        var otherIp = AnonymousRequest(provider, IPAddress.Parse("203.0.113.7"));
        foreach (var context in new[] { first, second, otherIp })
            await middleware.InvokeAsync(context, _ => { reached++; return Task.CompletedTask; });

        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.Equal("60", second.Response.Headers.RetryAfter.ToString());
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, first.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, otherIp.Response.StatusCode);
        Assert.Equal(2, reached);
    }

    [Fact]
    public async Task AnonymousLimit_NeverCountsAnAuthenticatedCaller()
    {
        using var provider = AnonymousLimitProvider();
        var middleware = provider.GetRequiredService<GatewayAnonymousRateLimitingMiddleware>();
        var reached = 0;

        for (var request = 0; request < 5; request++)
        {
            var context = AnonymousRequest(provider, IPAddress.Loopback);
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "client")], "test"));
            await middleware.InvokeAsync(context, _ => { reached++; return Task.CompletedTask; });
        }

        Assert.Equal(5, reached);
    }

    private static ServiceProvider AnonymousLimitProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:Local:Anonymous:PermitLimit"] = "1" })
            .Build();

        return new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
    }

    private static DefaultHttpContext AnonymousRequest(IServiceProvider services, IPAddress address, string? junkHeader = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = address;
        context.Response.Body = new MemoryStream();
        if (junkHeader is not null)
            context.Request.Headers[junkHeader] = "junk";
        return context;
    }

    [Fact]
    public void Fallback_IsBoundFromItsOwnSectionAndDefaultsToFailOpenAtHalf()
    {
        var defaults = ResolveOptions(new Dictionary<string, string?>());
        var configured = ResolveOptions(new Dictionary<string, string?>
        {
            ["RateLimiting:Distributed:Fallback:UseLocalFallbackWhenStoreUnavailable"] = "false",
            ["RateLimiting:Distributed:Fallback:LocalRatio"] = "0.25"
        });

        Assert.True(defaults.Distributed.Fallback.UseLocalFallbackWhenStoreUnavailable);
        Assert.Equal(0.5, defaults.Distributed.Fallback.LocalRatio);
        Assert.False(configured.Distributed.Fallback.UseLocalFallbackWhenStoreUnavailable);
        Assert.Equal(0.25, configured.Distributed.Fallback.LocalRatio);
    }

    private static GatewayRateLimitingOptions ResolveOptions(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value;
    }

    [Theory]
    [InlineData("Local:Anonymous:PermitLimit", "0")]
    [InlineData("Local:Service:QueueLimit", "-1")]
    [InlineData("Local:Instance:WindowSeconds", "86401")]
    [InlineData("Distributed:Authenticated:WindowSeconds", "0")]
    [InlineData("Distributed:Anonymous:QueueLimit", "1")]
    [InlineData("Distributed:Tenant:PermitLimit", "0")]
    [InlineData("Distributed:ApiKey:PermitLimit", "0")]
    [InlineData("Distributed:Fallback:LocalRatio", "0")]
    [InlineData("Distributed:Fallback:LocalRatio", "1.5")]
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
    [InlineData(true, null)]
    [InlineData(true, "redis.internal:6379")]
    [InlineData(false, null)]
    public void SharedQuotas_DoNotRequireRedis(bool distributedEnabled, string? redis)
    {
        var values = new Dictionary<string, string?> { ["RateLimiting:Distributed:Enabled"] = distributedEnabled.ToString() };
        if (redis is not null)
            values["Redis:ConnectionString"] = redis;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddLogging()
            .AddGatewayRateLimiting(configuration).BuildServiceProvider();

        _ = provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>().Value;
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
}
