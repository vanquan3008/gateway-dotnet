using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.Headers;
using Core.Gateway.Api.Resilience;

namespace Core.Gateway.Api.ReverseProxy;

public static class GatewayReverseProxyExtensions
{
    /// <summary>
    /// Loads YARP routes and clusters from config and adds the config filters, HTTP client settings and header transforms.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayReverseProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var resilience = configuration.GetSection(GatewayResilienceOptions.SectionName).Get<GatewayResilienceOptions>() ?? new();
        services
            .AddReverseProxy()
            .LoadFromConfig(configuration.GetRequiredSection(GatewayRouteOptions.ReverseProxySectionName))
            .AddConfigFilter<GatewayBackendHealthConfigFilter>()
            .AddConfigFilter<GatewayTimeoutConfigFilter>()
            .AddConfigFilter<GatewayRouteMetadataConfigFilter>()
            .ConfigureHttpClient((_, handler) =>
            {
                handler.ConnectTimeout = TimeSpan.FromSeconds(resilience.ConnectTimeoutSeconds);
                handler.PooledConnectionLifetime = TimeSpan.FromSeconds(resilience.PooledConnectionLifetimeSeconds);
            })
            .AddGatewayHeaderTransforms();

        return services;
    }
}
