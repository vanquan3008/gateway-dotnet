using Core.Gateway.Api.Authentication.ApiKey.Clients;
using Core.Gateway.Api.Authentication.ApiKey.Handler;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Authentication.ApiKey;

internal static class GatewayApiKeyExtensions
{
    /// <summary>
    /// Registers API key authentication when it is enabled.
    /// </summary>
    /// <param name="authentication">The authentication builder.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The API key header name, or null when disabled.</returns>
    public static string? AddGatewayApiKey(
        this AuthenticationBuilder authentication,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayApiKeyOptions.SectionName);
        var services = authentication.Services;

        services.AddSingleton<IValidateOptions<GatewayApiKeyOptions>, GatewayApiKeyOptionsValidator>();
        services.AddOptions<GatewayApiKeyOptions>()
            .Bind(section)
            .ValidateOnStart();

        var options = section.Get<GatewayApiKeyOptions>() ?? new();
        if (!options.Enabled)
        {
            return null;
        }
        services.AddSingleton<GatewayApiKeyClients>();
        authentication.AddScheme<AuthenticationSchemeOptions, GatewayApiKeyAuthenticationHandler>(GatewayApiKeyAuthenticationHandler.SchemeName, null);
        return options.HeaderName;
    }
}
