namespace Core.Gateway.Api.Configuration;

public static class GatewayConfigurationExtensions
{
    public static ConfigurationManager AddGatewayRoutes(this ConfigurationManager configuration, string[]? args = null)
    {
        var settings = configuration
            .GetSection(GatewayRouteOptions.SectionName)
            .Get<GatewayRouteOptions>() ?? new();

        if (string.IsNullOrWhiteSpace(settings.RoutesFile))
        {
            throw new InvalidOperationException("Gateway:RoutesFile is required.");
        }

        configuration.AddJsonFile(settings.RoutesFile, optional: false, reloadOnChange: true);
        configuration.AddEnvironmentVariables();
        if (args is { Length: > 0 })
        {
            configuration.AddCommandLine(args);
        }

        return configuration;
    }
}
