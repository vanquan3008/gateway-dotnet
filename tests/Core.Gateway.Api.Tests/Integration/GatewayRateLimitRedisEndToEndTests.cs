using System.Net;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Shared;
using Core.Gateway.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

[Collection("Redis rate limiting")]
public sealed class GatewayRateLimitRedisEndToEndTests
{
    private static async Task<WebApplication> StartAsync(string prefix, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Redis:ConnectionString"] = Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS"),
            ["Redis:KeyPrefix"] = prefix,
            ["RateLimiting:Distributed:Enabled"] = "true",
            ["RateLimiting:Distributed:Anonymous:PermitLimit"] = "3",
            ["RateLimiting:Distributed:Anonymous:WindowSeconds"] = "60",
            ["RateLimiting:Distributed:Fallback:HeartbeatSeconds"] = "1"
        };
        foreach (var (key, value) in extra ?? [])
            settings[key] = value;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddGatewayRateLimiting(builder.Configuration);
        var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<DistributedRateLimitingMiddleware>();
        app.MapGet("/ok", () => "OK");
        await app.StartAsync();
        return app;
    }

    private static async Task<int> WaitForPodCountAsync(WebApplication app, int expected)
    {
        var counter = app.Services.GetRequiredService<ActiveInstanceCounter>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (counter.Count != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        return counter.Count ?? 0;
    }

    [RedisFact]
    public async Task SharedQuota_ReportsHeadersOnAllowedAndRejectedResponses()
    {
        await using var app = await StartAsync($"test:e2e:{Guid.NewGuid():N}");
        using var client = app.GetTestClient();

        var remaining = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var allowed = await client.GetAsync("/ok");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal("3", allowed.Headers.GetValues(GatewayRateLimitHeaders.Limit).Single());
            remaining.Add(allowed.Headers.GetValues(GatewayRateLimitHeaders.Remaining).Single());
            Assert.InRange(int.Parse(allowed.Headers.GetValues(GatewayRateLimitHeaders.Reset).Single()), 1, 60);
        }

        using var rejected = await client.GetAsync("/ok");

        Assert.Equal(["2", "1", "0"], remaining);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("3", rejected.Headers.GetValues(GatewayRateLimitHeaders.Limit).Single());
        Assert.Equal("0", rejected.Headers.GetValues(GatewayRateLimitHeaders.Remaining).Single());
        Assert.Equal(
            rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString("0"),
            rejected.Headers.GetValues(GatewayRateLimitHeaders.Reset).Single());
    }

    [RedisFact]
    public async Task Heartbeat_CountsLivePodsAndDropsOnesThatLeave()
    {
        var prefix = $"test:e2e:{Guid.NewGuid():N}";
        await using var first = await StartAsync(prefix);
        await using var second = await StartAsync(prefix);

        Assert.Equal(2, await WaitForPodCountAsync(first, 2));
        Assert.Equal(2, await WaitForPodCountAsync(second, 2));

        await second.StopAsync();

        Assert.Equal(1, await WaitForPodCountAsync(first, 1));
    }

    [Fact]
    public async Task WithoutRedis_TheHeartbeatDoesNothingAndTheConfiguredRatioStays()
    {
        await using var app = await StartAsync($"test:e2e:{Guid.NewGuid():N}", new()
        {
            ["Redis:ConnectionString"] = null
        });

        await Task.Delay(1_500);

        Assert.Null(app.Services.GetRequiredService<ActiveInstanceCounter>().Count);
    }
}
