using Microsoft.Extensions.Diagnostics.HealthChecks;
using Yarp.ReverseProxy.Configuration;

namespace Core.Gateway.Api.HealthChecks;

public sealed class GatewayReadinessHealthCheck(IProxyConfigProvider proxyConfigProvider)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var proxyConfig = proxyConfigProvider.GetConfig();
        if (proxyConfig.Routes.Count == 0 || proxyConfig.Clusters.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("No proxy routes or clusters are configured."));
        }

        var clusters = proxyConfig.Clusters.ToDictionary(cluster => cluster.ClusterId, StringComparer.OrdinalIgnoreCase);

        foreach (var route in proxyConfig.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.ClusterId) || !clusters.ContainsKey(route.ClusterId))
            {
                return Task.FromResult(
                    HealthCheckResult.Unhealthy($"Route '{route.RouteId}' references an unknown cluster."));
            }
        }

        foreach (var cluster in proxyConfig.Clusters)
        {
            if (cluster.Destinations is null ||
                cluster.Destinations.Count == 0 ||
                cluster.Destinations.Values.Any(destination =>
                    !Uri.TryCreate(destination.Address, UriKind.Absolute, out var address) ||
                    address.Scheme is not ("http" or "https")))
            {
                return Task.FromResult(
                    HealthCheckResult.Unhealthy($"Cluster '{cluster.ClusterId}' has no valid destination."));
            }
        }

        return Task.FromResult(HealthCheckResult.Healthy("Proxy configuration is valid."));
    }
}
