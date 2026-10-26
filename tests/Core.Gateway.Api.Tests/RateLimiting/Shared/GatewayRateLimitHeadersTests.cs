using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting.Shared;

public sealed class GatewayRateLimitHeadersTests
{
    private static string[] Written(DefaultHttpContext context) =>
    [
        context.Response.Headers[GatewayRateLimitHeaders.Limit].ToString(),
        context.Response.Headers[GatewayRateLimitHeaders.Remaining].ToString(),
        context.Response.Headers[GatewayRateLimitHeaders.Reset].ToString()
    ];

    [Fact]
    public void WritesLimitRemainingAndResetSeconds()
    {
        var context = new DefaultHttpContext();

        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Allowed(TimeSpan.FromMilliseconds(2_300), 300, 41));
        GatewayRateLimitHeaders.Apply(context);

        Assert.Equal(["300", "41", "3"], Written(context));
    }

    [Fact]
    public void ReportsTheQuotaClosestToExhaustion()
    {
        var context = new DefaultHttpContext();

        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Allowed(TimeSpan.FromSeconds(30), 300, 250));
        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Allowed(TimeSpan.FromSeconds(10), 1000, 3));
        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Allowed(TimeSpan.FromSeconds(5), 5000, 4000));
        GatewayRateLimitHeaders.Apply(context);

        Assert.Equal(["1000", "3", "10"], Written(context));
    }

    [Fact]
    public void ARejectedQuotaShowsZeroRemainingAndTheRetryAfter()
    {
        var context = new DefaultHttpContext();

        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Allowed(TimeSpan.FromSeconds(30), 300, 5));
        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Rejected(TimeSpan.FromSeconds(42), 60));
        GatewayRateLimitHeaders.Apply(context);

        Assert.Equal(["60", "0", "42"], Written(context));
    }

    [Fact]
    public void WritesNothingWhenNoSharedStoreAnswered()
    {
        var context = new DefaultHttpContext();

        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Unavailable(TimeSpan.FromSeconds(5)));
        GatewayRateLimitHeaders.Observe(context, DistributedRateLimitResult.Allowed(TimeSpan.Zero));
        GatewayRateLimitHeaders.Apply(context);

        Assert.False(context.Response.Headers.ContainsKey(GatewayRateLimitHeaders.Limit));
    }
}
