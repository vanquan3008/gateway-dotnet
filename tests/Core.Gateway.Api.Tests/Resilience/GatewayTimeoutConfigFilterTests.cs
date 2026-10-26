using Core.Gateway.Api.Resilience;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Core.Gateway.Api.Tests.Resilience;

public sealed class GatewayTimeoutConfigFilterTests
{
    private static readonly GatewayTimeoutConfigFilter Filter =
        new(Options.Create(new GatewayResilienceOptions { ActivityTimeoutSeconds = 12 }));

    [Fact]
    public async Task ClusterWithoutActivityTimeout_GetsConfiguredDefault()
    {
        var version = new Version(2, 0);
        var result = await Filter.ConfigureClusterAsync(new ClusterConfig
        {
            ClusterId = "service",
            HttpRequest = new ForwarderRequestConfig { Version = version }
        }, default);

        Assert.Equal(TimeSpan.FromSeconds(12), result.HttpRequest!.ActivityTimeout);
        Assert.Equal(version, result.HttpRequest.Version);

        var empty = await Filter.ConfigureClusterAsync(new ClusterConfig { ClusterId = "empty" }, default);
        Assert.Equal(TimeSpan.FromSeconds(12), empty.HttpRequest!.ActivityTimeout);
    }

    [Fact]
    public async Task ExplicitActivityTimeout_IsPreserved()
    {
        var cluster = new ClusterConfig
        {
            ClusterId = "service",
            HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(90) }
        };

        Assert.Same(cluster, await Filter.ConfigureClusterAsync(cluster, default));
    }
}
