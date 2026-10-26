using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Core.Gateway.Api.Resilience;

/// <summary>
/// Applies the default activity timeout to clusters that do not set one.
/// </summary>
public sealed class GatewayTimeoutConfigFilter(
    IOptions<GatewayResilienceOptions> options) : IProxyConfigFilter
{
    private readonly TimeSpan _activityTimeout = TimeSpan.FromSeconds(options.Value.ActivityTimeoutSeconds);

    /// <summary>
    /// Sets the activity timeout when the cluster has none.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cluster.</returns>
    public ValueTask<ClusterConfig> ConfigureClusterAsync(
        ClusterConfig cluster,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (cluster.HttpRequest?.ActivityTimeout is not null)
        {
            return ValueTask.FromResult(cluster);
        }

        return ValueTask.FromResult(cluster with
        {
            HttpRequest = (cluster.HttpRequest ?? new ForwarderRequestConfig()) with { ActivityTimeout = _activityTimeout }
        });
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
}
