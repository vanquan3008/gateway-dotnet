using System.Globalization;
using Yarp.ReverseProxy.Model;

namespace Core.Gateway.Api.Configuration;

public static class GatewayRouteMetadata
{
    public const string RateLimitPolicy = "RateLimitPolicy";
    public const string RetryAttempts = "RetryAttempts";
    public const string MaxRequestBodySize = "MaxRequestBodySize";
    public const string Streaming = "Streaming";
    public const int MaxRetryAttempts = 3;
    public const long MaxRequestBodySizeLimit = 1_073_741_824;

    /// <summary>
    /// Reads a metadata value of the matched route.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="key">The metadata key.</param>
    /// <returns>The value, or null when there is no route or key.</returns>
    public static string? GetRouteMetadata(
        this HttpContext context,
        string key)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null)
        {
            return null;
        }
        var routeModel = endpoint.Metadata.GetMetadata<RouteModel>();
        if (routeModel is null)
        {
            return null;
        }
        var metadata = routeModel.Config.Metadata;
        if (metadata is null)
        {
            return null;
        }
        if (!metadata.TryGetValue(key, out var value))
        {
            return null;
        }
        return value;
    }

    /// <summary>
    /// Reads a route metadata value as a number.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <param name="key">The metadata key.</param>
    /// <returns>The number, or null when missing.</returns>
    /// <exception cref="FormatException">The value is not a number.</exception>
    public static long? GetRouteMetadataNumber(
        this HttpContext context,
        string key)
    {
        var value = context.GetRouteMetadata(key);
        if (value is null)
        {
            return null;
        }
        return long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
