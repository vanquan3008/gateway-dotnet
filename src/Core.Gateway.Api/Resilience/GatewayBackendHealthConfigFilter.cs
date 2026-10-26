using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Core.Gateway.Api.Resilience;

/// <summary>
/// Turns on passive backend health checks for clusters with several destinations.
/// </summary>
public sealed class GatewayBackendHealthConfigFilter(
    IOptions<GatewayResilienceOptions> options) : IProxyConfigFilter
{
    private readonly GatewayResilienceOptions _settings = options.Value;

    /// <summary>
    /// Adds passive health checking and the available-destinations policy to the cluster.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cluster.</returns>
    public ValueTask<ClusterConfig> ConfigureClusterAsync(
        ClusterConfig cluster,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_settings.BackendHealthCheckEnabled &&
            cluster.Destinations is { Count: > 1 } &&
            cluster.HealthCheck is null)
        {
            return ValueTask.FromResult(cluster with
            {
                HealthCheck = new HealthCheckConfig
                {
                    AvailableDestinationsPolicy = AvailableDestinationsPolicy,
                    Passive = new PassiveHealthCheckConfig
                    {
                        Enabled = true,
                        Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate,
                        ReactivationPeriod = TimeSpan.FromSeconds(_settings.BackendHealthReactivationSeconds)
                    },
                    Active = _settings.ActiveHealthCheckEnabled
                        ? new ActiveHealthCheckConfig
                        {
                            Enabled = true,
                            Interval = TimeSpan.FromSeconds(_settings.ActiveHealthCheckIntervalSeconds),
                            Timeout = TimeSpan.FromSeconds(_settings.ActiveHealthCheckTimeoutSeconds),
                            Policy = HealthCheckConstants.ActivePolicy.ConsecutiveFailures,
                            Path = _settings.ActiveHealthCheckPath
                        }
                        : null
                }
            });
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

    /// <summary>
    /// Returns the route unchanged.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="cluster">Its cluster.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route.</returns>
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
}
