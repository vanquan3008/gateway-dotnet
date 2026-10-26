using Core.Gateway.Api.Authentication.Signature.Clients;
using Core.Gateway.Api.Authentication.Signature.Handler;
using Core.Gateway.Api.Authentication.Signature.Replay;
using Core.Gateway.Api.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Authentication.Signature;

internal static class GatewaySignatureExtensions
{
    /// <summary>
    /// Registers signed-request authentication, with its clients and replay guard, when it is enabled.
    /// </summary>
    /// <param name="authentication">The authentication builder.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="requireProductionConfiguration">True to require Redis whenever replays are rejected.</param>
    /// <returns>The signature header name, or null when disabled.</returns>
    public static string? AddGatewaySignature(
        this AuthenticationBuilder authentication,
        IConfiguration configuration,
        bool requireProductionConfiguration = false)
    {
        var section = configuration.GetSection(GatewaySignatureOptions.SectionName);
        var services = authentication.Services;

        services.AddSingleton<IValidateOptions<GatewaySignatureOptions>, GatewaySignatureOptionsValidator>();
        var signature = services.AddOptions<GatewaySignatureOptions>()
            .Bind(section)
            .ValidateDataAnnotations();

        var options = section.Get<GatewaySignatureOptions>() ?? new();
        if (requireProductionConfiguration)
        {
            signature.Validate<IOptions<RedisOptions>>(
                (settings, redis) => !settings.Enabled || !settings.RejectReplays || redis.Value.IsConfigured,
                "Authentication:Signature:RejectReplays requires Redis:ConnectionString; without it a replay is only caught by the pod that saw the first request.");
        }

        signature.ValidateOnStart();

        if (!options.Enabled)
        {
            return null;
        }

        services.AddGatewayRedis(configuration);
        services.AddSingleton<GatewaySignatureClients>();
        services.AddSingleton<GatewaySignatureReplayGuard>();
        authentication.AddScheme<AuthenticationSchemeOptions, GatewaySignatureAuthenticationHandler>(
            GatewaySignatureAuthenticationHandler.SchemeName, null);
        return options.SignatureHeader;
    }
}
