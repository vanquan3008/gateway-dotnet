using Core.Gateway.Api.Shares;

namespace Core.Gateway.Api.Configuration.CORS;

public static class GatewayCorsExtensions
{
    public const string PolicyName = "gateway";

    public static IServiceCollection AddGatewayCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(GatewayCorsOptions.SectionName);
        var settings = section.Get<GatewayCorsOptions>() ?? new();

        services.AddOptions<GatewayCorsOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(options => !options.Enabled || options.AllowedOrigins.Length > 0, "Cors:AllowedOrigins must contain at least one origin when CORS is enabled.")
            .Validate(options => options.AllowedOrigins.All(GatewaySecurityValidators.IsValidCorsOrigin), "Cors:AllowedOrigins contains an invalid origin.")
            .ValidateOnStart();

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (!settings.Enabled)
            {
                return;
            }

            policy
                .WithOrigins(settings.AllowedOrigins)
                .WithMethods(settings.AllowedMethods)
                .WithHeaders(settings.AllowedHeaders)
                .WithExposedHeaders(Transformations.GatewayHeaders.CorrelationId);
        }));

        return services;
    }


}
