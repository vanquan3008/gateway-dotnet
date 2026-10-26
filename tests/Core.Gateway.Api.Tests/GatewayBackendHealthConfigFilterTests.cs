using Core.Gateway.Api.Resilience;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Core.Gateway.Api.Tests;

public sealed class GatewayBackendHealthConfigFilterTests
{
    private static GatewayBackendHealthConfigFilter Create(
        bool failWhenAllUnhealthy = true,
        bool healthCheckEnabled = true,
        int reactivationSeconds = 15) =>
        new(Options.Create(new GatewayResilienceOptions
        {
            BackendHealthCheckEnabled = healthCheckEnabled,
            BackendHealthReactivationSeconds = reactivationSeconds,
            FailWhenAllDestinationsUnhealthy = failWhenAllUnhealthy
        }));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Defaults_OnlyQuarantineIndependentDestinations(int destinations)
    {
        var cluster = new ClusterConfig
        {
            ClusterId = "service",
            Destinations = Enumerable.Range(0, destinations).ToDictionary(i => $"backend-{i}",
                _ => new DestinationConfig { Address = "http://backend/" })
        };
        var result = await Create().ConfigureClusterAsync(cluster, default);
        if (destinations == 1)
        {
            Assert.Same(cluster, result);
            Assert.Null(result.HealthCheck);
        }
        else
        {
            Assert.Equal(HealthCheckConstants.AvailableDestinations.HealthyAndUnknown, result.HealthCheck!.AvailableDestinationsPolicy);
            Assert.True(result.HealthCheck.Passive!.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(15), result.HealthCheck.Passive.ReactivationPeriod);
        }
    }

    [Fact]
    public async Task FailOpen_UsesHealthyOrPanicForIndependentDestinations()
    {
        var result = await Create(failWhenAllUnhealthy: false).ConfigureClusterAsync(new ClusterConfig
        {
            ClusterId = "service",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["a"] = new() { Address = "http://a/" },
                ["b"] = new() { Address = "http://b/" }
            }
        }, default);

        Assert.Equal(HealthCheckConstants.AvailableDestinations.HealthyOrPanic, result.HealthCheck!.AvailableDestinationsPolicy);
    }

    [Fact]
    public async Task DisabledHealth_DoesNotInjectDefaults()
    {
        var cluster = new ClusterConfig
        {
            ClusterId = "service",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["a"] = new() { Address = "http://a/" },
                ["b"] = new() { Address = "http://b/" }
            }
        };
        var result = await Create(healthCheckEnabled: false).ConfigureClusterAsync(cluster, default);
        Assert.Same(cluster, result);
        Assert.Null(result.HealthCheck);
    }

    [Fact]
    public async Task ExplicitHealthPolicy_IsPreserved()
    {
        var cluster = new ClusterConfig
        {
            ClusterId = "service",
            HealthCheck = new HealthCheckConfig
            {
                AvailableDestinationsPolicy = HealthCheckConstants.AvailableDestinations.HealthyOrPanic,
                Passive = new PassiveHealthCheckConfig { Enabled = false }
            },
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["a"] = new() { Address = "http://a/" },
                ["b"] = new() { Address = "http://b/" }
            }
        };
        Assert.Same(cluster, await Create().ConfigureClusterAsync(cluster, default));
    }

    [Fact]
    public async Task EnabledHealthWithoutDestinationPolicy_UsesConfiguredFailMode()
    {
        var active = new ActiveHealthCheckConfig { Enabled = true, Policy = "ConsecutiveFailures", Path = "/health/ready" };
        var result = await Create().ConfigureClusterAsync(new ClusterConfig
        {
            ClusterId = "service",
            HealthCheck = new HealthCheckConfig { Active = active }
        }, default);
        Assert.Same(active, result.HealthCheck!.Active);
        Assert.Equal(HealthCheckConstants.AvailableDestinations.HealthyAndUnknown, result.HealthCheck.AvailableDestinationsPolicy);
    }
}
