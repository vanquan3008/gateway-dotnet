using Microsoft.Extensions.Diagnostics.HealthChecks;
using Yarp.ReverseProxy.Configuration;

namespace Core.Gateway.Api.HealthChecks;

/// <summary>
/// Readiness check: the proxy config has routes and clusters with valid ids, destinations and references.
/// </summary>
public sealed class GatewayReadinessHealthCheck(
    IProxyConfigProvider proxyConfigProvider) : IHealthCheck
{
    /// <summary>
    /// Validates the loaded proxy config.
    /// </summary>
    /// <param name="context">The health check context.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Healthy, or Unhealthy with the first problem found.</returns>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = proxyConfigProvider.GetConfig();

        if (config.Routes.Count == 0 || config.Clusters.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No proxy routes or clusters configured."));
        }

        var clusterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cluster in config.Clusters)
        {
            var addSuccess = clusterIds.Add(cluster.ClusterId);
            if (string.IsNullOrWhiteSpace(cluster.ClusterId) || !addSuccess)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"Invalid or duplicate cluster ID: '{cluster.ClusterId}'."));
            }

            if (cluster.Destinations is not { Count: > 0 })
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"Cluster '{cluster.ClusterId}' has no destinations."));
            }

            foreach (var (destinationId, destination) in cluster.Destinations)
            {
                if (string.IsNullOrWhiteSpace(destination.Address) || !Uri.TryCreate(destination.Address, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") ||
                    string.IsNullOrWhiteSpace(uri.Host))
                {
                    return Task.FromResult(HealthCheckResult.Unhealthy($"Invalid destination '{destinationId}' in cluster '{cluster.ClusterId}'."));
                }
            }
        }

        foreach (var route in config.Routes)
        {
            var addSuccess = routeIds.Add(route.RouteId);
            if (string.IsNullOrWhiteSpace(route.RouteId) || !addSuccess)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"Invalid or duplicate route ID: '{route.RouteId}'."));
            }

            if (string.IsNullOrWhiteSpace(route.ClusterId) || !clusterIds.Contains(route.ClusterId))
            {
                return Task.FromResult(HealthCheckResult.Unhealthy($"Route '{route.RouteId}' references an unknown cluster '{route.ClusterId}'."));
            }
        }

        return Task.FromResult(HealthCheckResult.Healthy("Proxy configuration is ready."));
    }
}
