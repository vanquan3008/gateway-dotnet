using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Headers;

public static class GatewayHeaderExtensions
{
    /// <summary>
    /// Registers the claim-to-header options and their validator.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<GatewayHeaderOptions>, GatewayHeaderOptionsValidator>();
        services.AddOptions<GatewayHeaderOptions>()
            .Bind(configuration.GetSection(GatewayHeaderOptions.SectionName))
            .ValidateOnStart();
        return services;
    }
}
