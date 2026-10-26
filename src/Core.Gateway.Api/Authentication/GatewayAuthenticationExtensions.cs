using Core.Gateway.Api.HealthChecks;
using Core.Gateway.Api.Resilience;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication;

public static class GatewayAuthenticationExtensions
{
    public static IServiceCollection AddGatewayAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction)
    {
        var section = configuration.GetSection(GatewayAuthenticationOptions.SectionName);
        var settings = section.Get<GatewayAuthenticationOptions>() ?? new();
        var resilience = configuration.GetSection(GatewayResilienceOptions.SectionName).Get<GatewayResilienceOptions>() ?? new();

        services.AddOptions<GatewayAuthenticationOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Authority),
                "Authentication:Authority is required when authentication is enabled.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Audience),
                "Authentication:Audience is required when authentication is enabled.")
            .Validate(
                options => !isProduction || options.Enabled,
                "Authentication must be enabled in Production.")
            .Validate(
                options => !isProduction || options.RequireHttpsMetadata,
                "Authentication:RequireHttpsMetadata must be true in Production.")
            .Validate(
                options => !isProduction ||
                    Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) &&
                    authority.Scheme == Uri.UriSchemeHttps,
                "Authentication:Authority must use HTTPS in Production.")
            .ValidateOnStart();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = settings.Enabled ? settings.Authority : null;
                options.Audience = settings.Enabled ? settings.Audience : null;
                options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
                options.IncludeErrorDetails = !isProduction;
                options.MapInboundClaims = false;
                options.BackchannelTimeout = TimeSpan.FromMilliseconds(
                    Math.Min(settings.BackchannelTimeoutMilliseconds, resilience.RequestTimeoutSeconds * 1_000));
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds)
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = async context =>
                    {
                        if (!settings.Enabled ||
                            context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<GatewayHealthCheckMetadata>() is not null)
                        {
                            context.NoResult();
                            return;
                        }

                        var authorization = context.Request.Headers.Authorization.ToString();
                        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(authorization["Bearer ".Length..]) &&
                            context.Options.ConfigurationManager is { } manager)
                        {
                            // Resolve through the SDK cache before token validation can mask a retrieval failure.
                            try
                            {
                                await manager.GetConfigurationAsync(context.HttpContext.RequestAborted)
                                    .WaitAsync(context.HttpContext.RequestAborted);
                            }
                            catch (Exception exception) when (!context.HttpContext.RequestAborted.IsCancellationRequested &&
                                exception is InvalidOperationException or IOException or HttpRequestException or TimeoutException or OperationCanceledException)
                            {
                                GatewayAuthenticationFailureMiddleware.MarkMetadataUnavailable(context, exception);
                            }
                        }
                    }
                };
            });

        var authorization = services.AddAuthorizationBuilder();
        if (settings.Enabled)
        {
            authorization.SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        }

        return services;
    }
}
