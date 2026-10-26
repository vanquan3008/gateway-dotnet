using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting.Distributed;

public sealed class LocalFallbackQuotaTests
{
    private static readonly GatewayRateLimitPartition Partition = new(
        "user:alice",
        "user",
        new QuotaLimit { PermitLimit = 100, WindowSeconds = 60 });

    private static LocalFallbackQuota Create(ActiveInstanceCounter instances, bool autoScale = true) => new(
        Options.Create(new GatewayRateLimitingOptions
        {
            Distributed = new DistributedRateLimitingOptions
            {
                Fallback = new DistributedFallbackOptions { AutoScaleRatio = autoScale, LocalRatio = 0.5 }
            }
        }),
        instances);

    private static int Admitted(LocalFallbackQuota quota) => Enumerable.Range(0, 200)
        .Count(_ => quota.Resolve(Partition, storeConfigured: true, TimeSpan.Zero).Status == DistributedRateLimitStatus.Allowed);

    [Fact]
    public void WithoutAPodCount_UsesTheConfiguredRatio()
    {
        using var quota = Create(new ActiveInstanceCounter());

        Assert.Equal(50, Admitted(quota));
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(4, 25)]
    [InlineData(10, 10)]
    public void WithAPodCount_EachPodGetsItsShareOfTheQuota(int pods, int expectedPermits)
    {
        var instances = new ActiveInstanceCounter();
        instances.Set(pods);
        using var quota = Create(instances);

        Assert.Equal(expectedPermits, Admitted(quota));
    }

    [Fact]
    public void WhenThePodCountChanges_TheNewShareApplies()
    {
        var instances = new ActiveInstanceCounter();
        instances.Set(2);
        using var quota = Create(instances);
        Assert.Equal(50, Admitted(quota));

        instances.Set(10);

        Assert.Equal(10, Admitted(quota));
    }

    [Fact]
    public void WithAutoScaleOff_IgnoresThePodCount()
    {
        var instances = new ActiveInstanceCounter();
        instances.Set(10);
        using var quota = Create(instances, autoScale: false);

        Assert.Equal(50, Admitted(quota));
    }
}
