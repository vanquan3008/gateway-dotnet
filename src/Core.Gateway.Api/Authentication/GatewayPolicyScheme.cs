using Core.Gateway.Api.Authentication.ApiKey.Handler;
using Core.Gateway.Api.Authentication.Jwt;
using Core.Gateway.Api.Authentication.Signature.Handler;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication;

internal static class GatewayPolicyScheme
{
    private static readonly JsonWebTokenHandler TokenReader = new();

    /// <summary>
    /// Adds the policy scheme that picks the credential per request: API key header, signature header, otherwise the JWT scheme matching the token issuer.
    /// </summary>
    /// <param name="authentication">The authentication builder.</param>
    /// <param name="schemeName">Name of the policy scheme.</param>
    /// <param name="apiKeyHeader">API key header name, or null when API keys are off.</param>
    /// <param name="signatureHeader">Signature header name, or null when signatures are off.</param>
    /// <param name="multiProvider">True when several JWT providers are configured.</param>
    /// <param name="providers">The enabled JWT providers.</param>
    public static void AddGatewayPolicyScheme(
        this AuthenticationBuilder authentication,
        string schemeName,
        string? apiKeyHeader,
        string? signatureHeader,
        bool multiProvider,
        KeyValuePair<string, GatewayJwtProviderOptions>[] providers)
    {
        authentication.AddPolicyScheme(schemeName, null, options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                if (!string.IsNullOrWhiteSpace(apiKeyHeader) && context.Request.Headers.ContainsKey(apiKeyHeader))
                {
                    return GatewayApiKeyAuthenticationHandler.SchemeName;
                }

                if (!string.IsNullOrWhiteSpace(signatureHeader) && context.Request.Headers.ContainsKey(signatureHeader))
                {
                    return GatewaySignatureAuthenticationHandler.SchemeName;
                }

                if (!multiProvider)
                {
                    return JwtBearerDefaults.AuthenticationScheme;
                }

                if (!GatewayBearerToken.TryGet(context.Request, out var token) || !TokenReader.CanReadToken(token))
                {
                    return JwtBearerDefaults.AuthenticationScheme;
                }

                try
                {
                    var issuer = TokenReader.ReadJsonWebToken(token).Issuer;
                    foreach (var (name, provider) in providers)
                    {
                        if (string.Equals(issuer, provider.Issuer, StringComparison.Ordinal))
                        {
                            return GatewayAuthenticationExtensions.ProviderSchemePrefix + name;
                        }
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
                {
                    // Unreadable token: not swallowed, the default JWT scheme below rejects it and counts the failure.
                }

                return JwtBearerDefaults.AuthenticationScheme;
            };
        });
    }
}
