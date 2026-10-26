using System.Text.Encodings.Web;
using Core.Gateway.Api.Authentication.ApiKey.Clients;
using Core.Gateway.Api.Authentication.Metrics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Authentication.ApiKey.Handler;

/// <summary>
/// Authenticates requests that carry the API key header.
/// </summary>
internal sealed class GatewayApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    GatewayApiKeyClients clients)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string Issuer = "urn:core-gateway:api-key";

    /// <summary>
    /// Reads the API key header: no header gives no result, exactly one valid key succeeds, anything else fails.
    /// </summary>
    /// <returns>The authentication result.</returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKeys = Request.Headers[clients.HeaderName];

        if (apiKeys.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (apiKeys.Count != 1 || !clients.TryCreatePrincipal(apiKeys[0], out var principal))
        {
            var reason = apiKeys.Count != 1
                ? GatewayAuthenticationMetrics.Reasons.Malformed
                : GatewayAuthenticationMetrics.Reasons.InvalidKey;
            GatewayAuthenticationMetrics.Failed(GatewayAuthenticationMetrics.ApiKey, reason);
            Logger.LogInformation("API key authentication failed: {Reason}.", reason);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        GatewayAuthenticationMetrics.Succeeded(GatewayAuthenticationMetrics.ApiKey);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
