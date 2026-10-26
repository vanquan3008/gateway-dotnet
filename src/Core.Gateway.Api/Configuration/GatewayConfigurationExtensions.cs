namespace Core.Gateway.Api.Configuration;

public static class GatewayConfigurationExtensions
{
    /// <summary>
    /// Loads the routes file named by Gateway:RoutesFile, then environment variables and command-line arguments.
    /// </summary>
    /// <param name="configuration">The configuration manager.</param>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The same configuration manager.</returns>
    /// <exception cref="InvalidOperationException">Gateway:RoutesFile is not set.</exception>
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
