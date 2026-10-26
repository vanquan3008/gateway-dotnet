using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Core.Gateway.Api.Resilience;

public sealed class GatewayBackendHealthConfigFilter(
    IOptions<GatewayResilienceOptions> options) : IProxyConfigFilter
{
    private readonly GatewayResilienceOptions _settings = options.Value;

    public ValueTask<ClusterConfig> ConfigureClusterAsync(
        ClusterConfig cluster,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_settings.BackendHealthCheckEnabled &&
            cluster.Destinations is { Count: > 1 } &&
            cluster.HealthCheck is null)
        {
            return ValueTask.FromResult(cluster with { HealthCheck = CreateDefaultHealthCheck() });
        }

        if (cluster.HealthCheck is { AvailableDestinationsPolicy: null } healthCheck &&
            (healthCheck.Active?.Enabled == true || healthCheck.Passive?.Enabled == true))
        {
            return ValueTask.FromResult(cluster with
            {
                HealthCheck = healthCheck with { AvailableDestinationsPolicy = AvailableDestinationsPolicy }
            });
        }

        return ValueTask.FromResult(cluster);
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(
        RouteConfig route,
        ClusterConfig? cluster,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(route);
    }

    private string AvailableDestinationsPolicy =>
        _settings.FailWhenAllDestinationsUnhealthy
            ? HealthCheckConstants.AvailableDestinations.HealthyAndUnknown
            : HealthCheckConstants.AvailableDestinations.HealthyOrPanic;

    private HealthCheckConfig CreateDefaultHealthCheck() =>
        new()
        {
            AvailableDestinationsPolicy = AvailableDestinationsPolicy,
            Passive = new PassiveHealthCheckConfig
            {
                Enabled = true,
                Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate,
                ReactivationPeriod = TimeSpan.FromSeconds(_settings.BackendHealthReactivationSeconds)
            }
        };
}
