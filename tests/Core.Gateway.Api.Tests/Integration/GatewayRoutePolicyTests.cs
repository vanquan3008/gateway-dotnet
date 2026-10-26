using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Core.Gateway.Api.Headers;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    [Fact]
    public async Task RouteWithoutAPolicy_RequiresAnAuthenticatedCaller()
    {
        await using var host = await GatewayTestHost.StartAsync();

        using var anonymous = await host.Client.GetAsync("/api/orders");
        using var authenticated = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(1, host.DownstreamRequests);
    }

    [Fact]
    public async Task AnonymousRoute_NeedsNoCredentialWhileOthersStillDo()
    {
        await using var host = await GatewayTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:product-route:AuthorizationPolicy"] = "anonymous"
        });

        using var open = await host.Client.GetAsync("/api/products");
        using var protectedRoute = await host.Client.GetAsync("/api/orders");

        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, protectedRoute.StatusCode);
    }

    [Theory]
    [InlineData("ReverseProxy:Routes:order-route:AuthorizationPolicy")]
    [InlineData("ReverseProxy:Routes:order-route:Metadata:RateLimitPolicy")]
    public async Task UnknownRoutePolicy_FailsStartup(string setting)
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            GatewayTestHost.StartAsync(new Dictionary<string, string?> { [setting] = "missing" }));
    }

    [Fact]
    public async Task RouteRateLimitPolicy_SharesQuotaAcrossAliasesPerUserWithoutRedis()
    {
        await using var host = await GatewayTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["RateLimiting:Policies:strict:PermitLimit"] = "2",
            ["RateLimiting:Policies:strict:WindowSeconds"] = "60",
            ["ReverseProxy:Routes:order-route:Metadata:RateLimitPolicy"] = "strict",
            ["ReverseProxy:Routes:order-legacy-route:Metadata:RateLimitPolicy"] = "strict"
        });

        foreach (var path in new[] { "/api/orders", "/orders/1" })
        {
            using var allowed = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, path));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var rejected = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(int.Parse(rejected.Headers.RetryAfter!.ToString()) is > 0 and <= 60);

        using var otherUser = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders", userId: "user-b"));
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
        using var otherRoute = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, otherRoute.StatusCode);
        Assert.Equal(4, host.DownstreamRequests);
    }

    private static Dictionary<string, string?> RouteQuotaWithRedisDown(bool failOpen) => new()
    {
        ["RateLimiting:Policies:strict:PermitLimit"] = "2",
        ["RateLimiting:Policies:strict:WindowSeconds"] = "60",
        ["ReverseProxy:Routes:order-route:Metadata:RateLimitPolicy"] = "strict",
        ["RateLimiting:Distributed:Enabled"] = "true",
        ["RateLimiting:Distributed:Fallback:UseLocalFallbackWhenStoreUnavailable"] = failOpen ? "true" : "false",
        ["Redis:ConnectionString"] = "127.0.0.1:1",
        ["Redis:ConnectTimeoutMilliseconds"] = "100",
        ["Redis:TimeoutMilliseconds"] = "100"
    };

    [Fact]
    public async Task RouteQuota_WhenRedisIsDownAndFailOpen_FallsBackToHalfTheBudgetPerPod()
    {
        await using var host = await GatewayTestHost.StartAsync(RouteQuotaWithRedisDown(failOpen: true));

        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        using var second = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task RouteQuota_WhenRedisIsDownAndFailClosed_RejectsLikeTheClientQuotas()
    {
        await using var host = await GatewayTestHost.StartAsync(RouteQuotaWithRedisDown(failOpen: false));

        using var response = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, host.DownstreamRequests);

        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 5);
    }

    [Fact]
    public async Task ClaimsToHeaders_ForwardsConfiguredClaimsAndReplacesForgedValues()
    {
        await using var host = await GatewayTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Headers:ClaimsToHeaders:X-Merchant-Id"] = "merchant_id"
        });
        using var request = host.AuthenticatedRequest(HttpMethod.Get, "/api/users", tenantId: "tenant-1",
            claims: [new Claim("merchant_id", "merchant-1")]);
        request.Headers.Add(GatewayHeaders.MerchantId, "forged-merchant");
        request.Headers.Add(GatewayHeaders.TenantId, "forged-tenant");

        using var response = await host.Client.SendAsync(request);

        var echo = (await response.Content.ReadFromJsonAsync<ProxyEcho>())!;
        Assert.Equal("user-a", echo.UserId);
        Assert.Equal("tenant-1", echo.TenantId);
        Assert.Equal("merchant-1", echo.MerchantId);
    }

    [Theory]
    [InlineData("/health/live", "8081", HttpStatusCode.OK)]
    [InlineData("/health/ready", "8081", HttpStatusCode.OK)]
    [InlineData("/health/live", null, HttpStatusCode.NotFound)]
    [InlineData("/api/users", "8081", HttpStatusCode.NotFound)]
    [InlineData("/api/users", null, HttpStatusCode.OK)]
    public async Task ManagementPort_SeparatesProbesFromProxiedTraffic(string path, string? localPort, HttpStatusCode expected)
    {
        await using var host = await GatewayTestHost.StartAsync(new Dictionary<string, string?>
        {
            ["HealthChecks:ManagementPort"] = "8081"
        });
        using var request = host.AuthenticatedRequest(HttpMethod.Get, path);
        if (localPort is not null)
            request.Headers.Add("X-Test-Local-Port", localPort);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }
}
