using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Interfaces;
using Core.Gateway.Api.RateLimiting.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting.Distributed;

[Collection("Redis rate limiting")]
public sealed class DistributedRateLimitingMiddlewareTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ApiKeyQuota_OnlyUsesAuthenticatedIdClaim(bool authenticated, bool hasClaim)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Distributed:Enabled"] = "true",
            ["RateLimiting:Distributed:ApiKey:PermitLimit"] = "1"
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(hasClaim ? [new Claim("api_key_id", "trusted-id")] : [],
            authenticated ? "test" : null));
        context.Request.Headers["X-Api-Key"] = "forged-id";
        var reached = false;
        var middleware = new DistributedRateLimitingMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>());
        var store = new ApiKeyQuotaStore();
        await middleware.InvokeAsync(context, store);
        var applies = authenticated && hasClaim;
        Assert.Equal(!applies, reached);
        Assert.Equal(applies, store.Keys.Contains("api-key:trusted-id"));
        Assert.DoesNotContain("api-key:forged-id", store.Keys);
        if (applies)
        {
            Assert.Equal(429, context.Response.StatusCode);
            Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
        }
    }

    [Fact]
    public async Task PartialRedisFailure_BypassesUnavailableQuotaWithoutCreatingLocalTenantBudgetAndEmitsBoundedMetrics()
    {
        var measurements = new List<(string Name, string? Type)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == DistributedRateLimitingMiddleware.MeterName)
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            Assert.Equal(1, tags.Length);
            Assert.Equal("partition.type", tags[0].Key);
            measurements.Add((instrument.Name, tags[0].Value?.ToString()));
        });
        listener.Start();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Distributed:Enabled"] = "true",
            ["RateLimiting:Distributed:RedisConnectionString"] = "localhost:6379",
            ["RateLimiting:Distributed:Authenticated:PermitLimit"] = "1",
            ["RateLimiting:Distributed:Tenant:PermitLimit"] = "2"
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddGatewayRateLimiting(configuration).BuildServiceProvider();
        using var limiter = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter!;
        var reached = 0;
        var middleware = new DistributedRateLimitingMiddleware(_ =>
        {
            reached++;
            return Task.CompletedTask;
        }, provider.GetRequiredService<IOptions<GatewayRateLimitingOptions>>());
        var store = new PartiallyUnavailableStore();
        for (var i = 0; i < 3; i++)
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-a"), new Claim("tenant_id", "tenant-a")], "test"))
            };
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            using var lease = limiter.AttemptAcquire(context);
            Assert.True(lease.IsAcquired);
            await middleware.InvokeAsync(context, store);
        }
        // A tenant limit of two must never become a per-pod fallback counter.
        Assert.Equal(3, reached);
        Assert.Equal(3, measurements.Count(m => m == ("gateway.ratelimit.distributed.allowed", "user")));
        Assert.Equal(3, measurements.Count(m => m == ("gateway.ratelimit.redis.unavailable", "tenant")));
        Assert.Equal(3, measurements.Count(m => m == ("gateway.ratelimit.distributed.bypassed", "tenant")));
        Assert.DoesNotContain(measurements, m => m.Name == "gateway.ratelimit.local.fallback");
    }

    private sealed class PartiallyUnavailableStore : IDistributedRateLimitStore
    {
        public ValueTask<DistributedRateLimitResult> AcquireAsync(string partitionKey, int permitLimit,
            TimeSpan window, CancellationToken cancellationToken) =>
            ValueTask.FromResult(partitionKey.StartsWith("user:", StringComparison.Ordinal)
                ? DistributedRateLimitResult.Allowed(window)
                : DistributedRateLimitResult.Unavailable);
    }

    private sealed class ApiKeyQuotaStore : IDistributedRateLimitStore
    {
        public List<string> Keys { get; } = [];

        public ValueTask<DistributedRateLimitResult> AcquireAsync(string partitionKey, int permitLimit,
            TimeSpan window, CancellationToken cancellationToken)
        {
            Keys.Add(partitionKey);
            return ValueTask.FromResult(partitionKey.StartsWith("api-key:", StringComparison.Ordinal)
                ? DistributedRateLimitResult.Rejected(window)
                : DistributedRateLimitResult.Allowed(window));
        }
    }
}
