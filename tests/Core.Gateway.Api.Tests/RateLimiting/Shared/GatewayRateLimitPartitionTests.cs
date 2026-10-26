using System.Net;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting.Shared;

public sealed class GatewayRateLimitPartitionTests
{
    [Theory]
    [InlineData("203.0.113.9", "ip:203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "ip:203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "ip:2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1111:2222:3333:4444", "ip:2001:db8:1:2::/64")]
    public void ForAnonymous_KeysIpv4PerAddressAndIpv6PerSlash64(string address, string expectedKey)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        var partition = GatewayRateLimitPartition.ForAnonymous(context, new QuotaLimit { PermitLimit = 1 });

        Assert.Equal(expectedKey, partition.Key);
        Assert.Equal("ip", partition.Type);
    }

    [Fact]
    public void ForAnonymous_SeparatesDifferentIpv6Networks()
    {
        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();
        first.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8:1:2::1");
        second.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8:1:3::1");
        var limit = new QuotaLimit { PermitLimit = 1 };

        Assert.NotEqual(
            GatewayRateLimitPartition.ForAnonymous(first, limit).Key,
            GatewayRateLimitPartition.ForAnonymous(second, limit).Key);
    }
}

public sealed class FixedWindowLimitScaleTests
{
    [Theory]
    [InlineData(300, 0.5, 150)]
    [InlineData(5, 0.5, 2)]
    [InlineData(1, 0.5, 1)]
    [InlineData(100, 1.0, 100)]
    public void Scale_FloorsToAtLeastOnePermitAndKeepsWindow(int permits, double ratio, int expected)
    {
        var scaled = new QuotaLimit { PermitLimit = permits, WindowSeconds = 30, QueueLimit = 5 }.Scale(ratio);

        Assert.Equal(expected, scaled.PermitLimit);
        Assert.Equal(30, scaled.WindowSeconds);
        Assert.Equal(0, scaled.QueueLimit);
    }
}
