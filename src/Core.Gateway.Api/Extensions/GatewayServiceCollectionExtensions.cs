using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.Resilience;
using Core.Gateway.Api.Transformations;

namespace Core.Gateway.Api.Extensions;

public static class GatewayServiceCollectionExtensions
{
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
            .ConfigureHttpClient((_, handler) =>
            {
                handler.ConnectTimeout = TimeSpan.FromSeconds(resilience.ConnectTimeoutSeconds);
                handler.PooledConnectionLifetime = TimeSpan.FromSeconds(resilience.PooledConnectionLifetimeSeconds);
            })
            .AddGatewayTransforms();

        return services;
    }
}
