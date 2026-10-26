using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Core.Gateway.Api.Resilience;

public sealed class GatewayTimeoutConfigFilter(
    IOptions<GatewayResilienceOptions> options) : IProxyConfigFilter
{
    private readonly TimeSpan _activityTimeout = TimeSpan.FromSeconds(options.Value.ActivityTimeoutSeconds);

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

    public ValueTask<RouteConfig> ConfigureRouteAsync(
        RouteConfig route,
        ClusterConfig? cluster,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(route);
    }
}
