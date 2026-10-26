using Core.Gateway.Api.Authentication.ApiKey;
using Core.Gateway.Api.Authentication.Jwt;
using Core.Gateway.Api.Authentication.Signature;
using Microsoft.AspNetCore.Authorization;

namespace Core.Gateway.Api.Authentication;

public static class GatewayAuthenticationExtensions
{
    public const string ProviderSchemePrefix = "GatewayJwt:";
    private const string GatewayScheme = "Gateway";

    /// <summary>
    /// Registers API key, signature and JWT authentication behind one policy scheme and requires an authenticated caller by default. With Authentication:Enabled off it only binds options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="isProduction">True to enforce production rules (authentication must be on).</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction)
    {
        var section = configuration.GetSection(GatewayAuthenticationOptions.SectionName);
        var settings = section.Get<GatewayAuthenticationOptions>() ?? new();

        services.AddOptions<GatewayAuthenticationOptions>()
            .Bind(section)
            .Validate(options => !isProduction || options.Enabled, "Authentication must be enabled in Production.")
            .ValidateOnStart();

        var authentication = services.AddAuthentication(GatewayScheme);

        var authorization = services.AddAuthorizationBuilder();

        if (!settings.Enabled)
        {

            services.AddOptions<GatewayApiKeyOptions>().Bind(configuration.GetSection(GatewayApiKeyOptions.SectionName));
            services.AddOptions<GatewaySignatureOptions>().Bind(configuration.GetSection(GatewaySignatureOptions.SectionName));
            return services;
        }

        authorization.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        var apiKeyHeader = authentication.AddGatewayApiKey(configuration);
        var signatureHeader = authentication.AddGatewaySignature(configuration, isProduction);

        var jwt = authentication.AddGatewayJwt(
            configuration,
            isProduction,
            isOptional: apiKeyHeader is not null || signatureHeader is not null);

        authentication.AddGatewayPolicyScheme(
            GatewayScheme,
            apiKeyHeader,
            signatureHeader,
            jwt.Providers.Count > 0,
            jwt.Providers.Where(pair => pair.Value.Enabled).ToArray());

        return services;
    }
}
