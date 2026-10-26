using System.Globalization;
using Core.Gateway.Api.RateLimiting;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace Core.Gateway.Api.Configuration;

/// <summary>
/// Checks route metadata when the proxy config loads (rate limit policy, retry attempts, max body size) and applies streaming routes.
/// </summary>
public sealed class GatewayRouteMetadataConfigFilter(
    IOptions<GatewayRateLimitingOptions> rateLimiting) : IProxyConfigFilter
{
    private const string DisableTimeoutPolicy = "Disable";

    /// <summary>
    /// Returns the cluster unchanged.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The cluster.</returns>
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancellationToken) =>
        ValueTask.FromResult(cluster);

    /// <summary>
    /// Validates the route metadata; a streaming route comes back without a request timeout.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="cluster">Its cluster.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The route.</returns>
    /// <exception cref="InvalidOperationException">The metadata is invalid.</exception>
    public ValueTask<RouteConfig> ConfigureRouteAsync(
        RouteConfig route,
        ClusterConfig? cluster,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (route.Metadata is not { } metadata)
        {
            return ValueTask.FromResult(route);
        }

        if (metadata.TryGetValue(GatewayRouteMetadata.RateLimitPolicy, out var policy) &&
            !rateLimiting.Value.Policies.ContainsKey(policy))
        {
            throw Invalid(route, $"references unknown rate limit policy '{policy}'; define it under RateLimiting:Policies");
        }

        if (metadata.TryGetValue(GatewayRouteMetadata.RetryAttempts, out var retries) &&
            !IsInRange(retries, 0, GatewayRouteMetadata.MaxRetryAttempts))
        {
            throw Invalid(route, $"{GatewayRouteMetadata.RetryAttempts} must be 0-{GatewayRouteMetadata.MaxRetryAttempts}");
        }

        if (metadata.TryGetValue(GatewayRouteMetadata.MaxRequestBodySize, out var bodySize) &&
            !IsInRange(bodySize, 1, GatewayRouteMetadata.MaxRequestBodySizeLimit))
        {
            throw Invalid(route, $"{GatewayRouteMetadata.MaxRequestBodySize} must be 1-{GatewayRouteMetadata.MaxRequestBodySizeLimit} bytes");
        }

        if (metadata.TryGetValue(GatewayRouteMetadata.Streaming, out var streaming))
        {
            if (!bool.TryParse(streaming, out var isStreaming))
            {
                throw Invalid(route, $"{GatewayRouteMetadata.Streaming} must be 'true' or 'false'");
            }

            if (isStreaming)
            {
                return ValueTask.FromResult(DisableRequestTimeout(route));
            }
        }

        return ValueTask.FromResult(route);
    }

    /// <summary>
    /// Turns the request timeout off for a streaming route; the cluster activity timeout still ends idle streams.
    /// </summary>
    /// <param name="route">The streaming route.</param>
    /// <returns>The route without a request timeout.</returns>
    /// <exception cref="InvalidOperationException">The route also names a timeout policy.</exception>
    private static RouteConfig DisableRequestTimeout(RouteConfig route)
    {
        if (route.TimeoutPolicy is not null &&
            !string.Equals(route.TimeoutPolicy, DisableTimeoutPolicy, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(route, $"{GatewayRouteMetadata.Streaming} cannot be combined with TimeoutPolicy '{route.TimeoutPolicy}'");
        }

        return route with { TimeoutPolicy = DisableTimeoutPolicy };
    }

    /// <summary>
    /// Whether a string is a non-negative integer between minimum and maximum.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="minimum">Lowest allowed.</param>
    /// <param name="maximum">Highest allowed.</param>
    /// <returns>True when in range.</returns>
    private static bool IsInRange(string value, long minimum, long maximum) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
        number >= minimum && number <= maximum;

    /// <summary>
    /// Builds the error for an invalid route.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <param name="problem">What is wrong.</param>
    /// <returns>The exception.</returns>
    private static InvalidOperationException Invalid(RouteConfig route, string problem) =>
        new($"Route '{route.RouteId}' {problem}.");
}
