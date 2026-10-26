using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Core.Gateway.Api.Authentication.Metrics;
using Core.Gateway.Api.Authentication.Signature.Clients;
using Core.Gateway.Api.Authentication.Signature.Replay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;
using static Core.Gateway.Api.Authentication.Metrics.GatewayAuthenticationMetrics;

namespace Core.Gateway.Api.Authentication.Signature.Handler;

/// <summary>
/// Authenticates requests signed by registered clients.
/// </summary>
internal sealed class GatewaySignatureAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    GatewaySignatureClients clients,
    GatewaySignatureReplayGuard replayGuard)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Signature";
    public const string Issuer = "urn:core-gateway:signature";

    private const int MaxLoggedClientIdLength = 64;

    private readonly record struct SignatureHeaders(
        string ClientId,
        string Signature,
        string Timestamp,
        long IssuedAt);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers[clients.SignatureHeader].Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (!TryReadHeaders(out var headers))
        {
            return Reject(Reasons.Malformed);
        }

        var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();

        if (Math.Abs((double)now - headers.IssuedAt) > clients.MaxClockSkew.TotalSeconds)
        {
            return Reject(Reasons.Expired);
        }

        if (!clients.Knows(headers.ClientId))
        {
            return Reject(Reasons.UnknownClient);
        }

        if (Request.ContentLength > clients.MaxBodyBytes)
        {
            return Reject(Reasons.BodyTooLarge);
        }

        Request.EnableBuffering(
            bufferThreshold: clients.MaxBodyBytes,
            bufferLimit: clients.MaxBodyBytes);

        string bodyHash;

        try
        {
            var hash = await SHA256.HashDataAsync(
                Request.Body,
                Context.RequestAborted);

            Request.Body.Position = 0;
            bodyHash = Convert.ToHexStringLower(hash);
        }
        catch (IOException)
        {
            return Reject(Reasons.BodyTooLarge);
        }

        var signedText =
            $"{Request.Method.ToUpperInvariant()}\n" +
            $"{Request.GetEncodedPathAndQuery()}\n" +
            $"{headers.Timestamp}\n" +
            $"{headers.ClientId}\n" +
            bodyHash;

        if (!clients.TryAuthenticate(
                headers.ClientId,
                signedText,
                headers.Signature,
                out var principal))
        {
            return Reject(Reasons.InvalidSignature);
        }

        if (clients.RejectReplays &&
            !await replayGuard.TryRegisterAsync(
                headers.ClientId,
                headers.Signature,
                Context.RequestAborted))
        {
            return Reject(Reasons.Replayed);
        }

        GatewayAuthenticationMetrics.Succeeded(
            GatewayAuthenticationMetrics.Signature);

        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName));
    }

    private bool TryReadHeaders(out SignatureHeaders headers)
    {
        headers = default;

        var clientIds = Request.Headers[clients.ClientIdHeader];
        var signatures = Request.Headers[clients.SignatureHeader];
        var timestamps = Request.Headers[clients.TimestampHeader];

        if (clientIds.Count != 1 ||
            signatures.Count != 1 ||
            timestamps.Count != 1 ||
            clientIds[0] is not { } clientId ||
            signatures[0] is not { } signature ||
            timestamps[0] is not { } timestamp ||
            !long.TryParse(
                timestamp,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var issuedAt))
        {
            return false;
        }

        headers = new SignatureHeaders(
            clientId,
            signature,
            timestamp,
            issuedAt);

        return true;
    }

    private AuthenticateResult Reject(string reason)
    {
        var clientIds = Request.Headers[clients.ClientIdHeader];

        var clientId = clientIds.Count == 1 &&
            clientIds[0] is { Length: > 0 and <= MaxLoggedClientIdLength } value ? value : "-";

        GatewayAuthenticationMetrics.Failed(
            GatewayAuthenticationMetrics.Signature,
            reason);

        Logger.LogInformation(
            "Signature authentication failed for client {ClientId}: {Reason}.",
            clientId,
            reason);

        return AuthenticateResult.Fail("Invalid signature.");
    }
}
