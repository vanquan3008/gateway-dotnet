using System.Collections.Frozen;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Authentication.Signature.Clients;

/// <summary>
/// Holds the enabled signing clients and verifies their signatures.
/// </summary>
internal sealed class GatewaySignatureClients
{
    private const int MaxSignatureLength = 1024;

    private readonly FrozenDictionary<string, GatewaySignatureClient> _clients;
    private readonly RSASignaturePadding _padding;
    public string ClientIdHeader { get; }

    public string SignatureHeader { get; }

    public string TimestampHeader { get; }

    public TimeSpan MaxClockSkew { get; }

    public int MaxBodyBytes { get; }

    public bool RejectReplays { get; }

    /// <summary>
    /// Whether a client id is registered and enabled.
    /// </summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>True when known.</returns>
    public bool Knows(string clientId) => _clients.ContainsKey(clientId);
    /// <summary>
    /// Loads the enabled signing clients with their public keys.
    /// </summary>
    /// <param name="signatureOptions">The signature settings.</param>
    /// <param name="rateLimitingOptions">Gives the API key and tenant claim names.</param>
    public GatewaySignatureClients(
        IOptions<GatewaySignatureOptions> signatureOptions,
        IOptions<GatewayRateLimitingOptions> rateLimitingOptions)
    {
        var settings = signatureOptions.Value;

        ClientIdHeader = settings.ClientIdHeader;
        SignatureHeader = settings.SignatureHeader;
        TimestampHeader = settings.TimestampHeader;
        MaxClockSkew = TimeSpan.FromSeconds(settings.MaxClockSkewSeconds);
        MaxBodyBytes = settings.MaxBodyBytes;
        RejectReplays = settings.RejectReplays;

        _padding = GetPadding(settings.Algorithm);
        _clients = LoadClients(settings, rateLimitingOptions.Value.Distributed);
    }


    /// <summary>
    /// Verifies a signature of the signed text with the client's public keys.
    /// </summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="signedText">The text that was signed.</param>
    /// <param name="signature">Base64 signature.</param>
    /// <param name="principal">The client identity when valid.</param>
    /// <returns>True when a key verifies the signature.</returns>
    public bool TryAuthenticate(
        string clientId,
        string signedText,
        string signature,
        out ClaimsPrincipal principal)
    {
        principal = null!;

        if (signature.Length > MaxSignatureLength || !_clients.TryGetValue(clientId, out var client))
        {
            return false;
        }

        if (!TryDecodeSignature(signature, out var signatureBytes))
        {
            return false;
        }

        var data = Encoding.UTF8.GetBytes(signedText);

        if (!client.Verify(data, signatureBytes.Span, _padding))
        {
            return false;
        }

        principal = client.CreatePrincipal();
        return true;
    }

    /// <summary>
    /// Builds the enabled clients by name.
    /// </summary>
    private static FrozenDictionary<string, GatewaySignatureClient> LoadClients(
        GatewaySignatureOptions settings,
        DistributedRateLimitingOptions claimNames)
    {
        return settings.Clients
            .Where(entry => entry.Value.Enabled)
            .ToFrozenDictionary(
                entry => entry.Key,
                entry => GatewaySignatureClient.Create(
                    entry.Key,
                    entry.Value,
                    claimNames.ApiKeyIdClaim,
                    claimNames.TenantIdClaim),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Maps the configured algorithm to its RSA padding: PS256 is PSS, RS256 is PKCS#1.
    /// </summary>
    private static RSASignaturePadding GetPadding(string algorithm)
    {
        return algorithm == GatewaySignatureOptions.Ps256 ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1;
    }

    /// <summary>
    /// Decodes a Base64 signature.
    /// </summary>
    /// <param name="signature">Base64 signature.</param>
    /// <param name="bytes">The raw signature when valid.</param>
    /// <returns>True when the text is valid Base64.</returns>
    private static bool TryDecodeSignature(string signature, out ReadOnlyMemory<byte> bytes)
    {
        var buffer = new byte[signature.Length];

        if (!Convert.TryFromBase64String(signature, buffer, out var length))
        {
            bytes = default;
            return false;
        }

        bytes = buffer.AsMemory(0, length);
        return true;
    }
}
