using Core.Gateway.Api.Authentication.Metrics;
using Core.Gateway.Api.Authentication.Middleware;
using Core.Gateway.Api.HealthChecks;
using Core.Gateway.Api.Resilience;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication.Jwt;

internal static class GatewayJwtBearerExtensions
{
    private static readonly string[] AuthorityAlgorithms = ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];
    /// <summary>
    /// Registers JWT bearer authentication: one authority, or one scheme per configured provider.
    /// </summary>
    /// <param name="authentication">The authentication builder.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="isProduction">True in production.</param>
    /// <param name="isOptional">True when API key or signature auth is also enabled, so JWT settings may be absent.</param>
    /// <returns>The bound JWT options.</returns>
    public static GatewayJwtOptions AddGatewayJwt(
        this AuthenticationBuilder authentication,
        IConfiguration configuration,
        bool isProduction,
        bool isOptional)
    {
        var section = configuration.GetSection(GatewayJwtOptions.SectionName);
        var settings = section.Get<GatewayJwtOptions>() ?? new();
        var resilience = configuration.GetSection(GatewayResilienceOptions.SectionName).Get<GatewayResilienceOptions>() ?? new();
        var providers = settings.Providers.Where(pair => pair.Value.Enabled).ToArray();

        authentication.Services.AddSingleton<IValidateOptions<GatewayJwtOptions>>(
            new GatewayJwtOptionsValidator(isProduction, isOptional));
        authentication.Services.AddOptions<GatewayJwtOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (settings.Providers.Count == 0)
        {
            authentication.AddJwtBearer(options => ConfigureBearer(
                options,
                settings, resilience,
                isProduction,
                null,
                JwtBearerDefaults.AuthenticationScheme));
            return settings;
        }

        authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    context.NoResult();
                    return Task.CompletedTask;
                }
            };
        });
        foreach (var (name, provider) in providers)
        {
            var scheme = GatewayAuthenticationExtensions.ProviderSchemePrefix + name;
            authentication.AddJwtBearer(scheme, options => ConfigureBearer(
                options, settings, resilience, isProduction, provider, scheme));
        }

        return settings;
    }

    /// <summary>
    /// Applies validation rules, metrics and the identity-provider-unavailable check to one JWT bearer scheme.
    /// </summary>
    /// <param name="options">The bearer options to fill in.</param>
    /// <param name="settings">The JWT settings.</param>
    /// <param name="resilience">Timeout settings.</param>
    /// <param name="isProduction">True in production.</param>
    /// <param name="provider">The provider, or null for the single-authority setup.</param>
    /// <param name="scheme">The scheme name.</param>
    private static void ConfigureBearer(
        JwtBearerOptions options,
        GatewayJwtOptions settings,
        GatewayResilienceOptions resilience,
        bool isProduction,
        GatewayJwtProviderOptions? provider,
        string scheme)
    {
        options.Authority = provider is null ? settings.Authority : provider.Authority;
        options.Audience = provider is null ? settings.Audience : provider.Audience;
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
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidIssuer = provider?.Issuer,
            IssuerSigningKeys = provider?.CreateSigningKeys(),
            ValidAlgorithms = provider?.ValidAlgorithms ?? AuthorityAlgorithms,
            NameClaimType = provider?.NameClaimType ?? "sub",
            RoleClaimType = provider?.RoleClaimType ?? "role",
            IgnoreTrailingSlashWhenValidatingAudience = provider is null,
            ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds)
        };

        if (provider is not null)
        {
            options.TokenValidationParameters.AuthenticationType = scheme;
            options.TokenValidationParameters.IssuerValidator = (issuer, _, _) => string.Equals(issuer, provider.Issuer, StringComparison.Ordinal) ? issuer
                    : throw new SecurityTokenInvalidIssuerException("Token issuer does not match the configured provider.");
        }

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = _ =>
            {
                GatewayAuthenticationMetrics.Succeeded(GatewayAuthenticationMetrics.Jwt);
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {

                GatewayAuthenticationMetrics.Failed(GatewayAuthenticationMetrics.Jwt, context.Exception switch
                {
                    SecurityTokenExpiredException => GatewayAuthenticationMetrics.Reasons.Expired,
                    SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException => GatewayAuthenticationMetrics.Reasons.InvalidSignature,
                    SecurityTokenInvalidAudienceException => GatewayAuthenticationMetrics.Reasons.InvalidAudience,
                    SecurityTokenInvalidIssuerException => GatewayAuthenticationMetrics.Reasons.InvalidIssuer,
                    _ => GatewayAuthenticationMetrics.Reasons.InvalidToken
                });
                return Task.CompletedTask;
            },
            OnMessageReceived = async context =>
            {
                var httpContext = context.HttpContext;
                if (httpContext.GetEndpoint()?.Metadata.GetMetadata<GatewayHealthCheckMetadata>() is not null)
                {
                    context.NoResult();
                    return;
                }

                if (!GatewayBearerToken.TryGet(context.Request, out _) ||
                    context.Options.ConfigurationManager is not { } manager)
                {
                    return;
                }
                try
                {
                    await manager.GetConfigurationAsync(httpContext.RequestAborted)
                        .WaitAsync(httpContext.RequestAborted);
                }
                catch (Exception exception) when (
                    !httpContext.RequestAborted.IsCancellationRequested &&
                    exception is InvalidOperationException
                        or IOException
                        or HttpRequestException
                        or TimeoutException
                        or OperationCanceledException)
                {
                    GatewayAuthenticationFailureMiddleware.MarkMetadataUnavailable(context, exception);
                }
            }
        };
    }
}
