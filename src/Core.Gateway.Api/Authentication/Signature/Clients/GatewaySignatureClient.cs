using System.Security.Claims;
using System.Security.Cryptography;
using Core.Gateway.Api.Authentication.Keys;
using Core.Gateway.Api.Authentication.Signature.Handler;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication.Signature.Clients;

/// <summary>
/// One enabled signing client: the identity it authenticates as and the public keys that verify it.
/// </summary>
internal sealed class GatewaySignatureClient
{
    private const string SubjectClaim = "sub";
    private const string IssuerClaim = "iss";
    private const string ScopeClaim = "scope";
    private const string RoleClaim = "role";

    private readonly Claim[] _claims;
    private readonly RSA[] _keys;

    private GatewaySignatureClient(Claim[] claims, RSA[] keys)
    {
        _claims = claims;
        _keys = keys;
    }

    /// <summary>
    /// Builds a client from its settings: claims and public keys loaded from the configured file.
    /// </summary>
    /// <param name="name">The client name.</param>
    /// <param name="options">The client settings.</param>
    /// <param name="apiKeyIdClaim">Claim name for the client id.</param>
    /// <param name="tenantIdClaim">Claim name for the tenant id.</param>
    /// <returns>The client.</returns>
    public static GatewaySignatureClient Create(
        string name,
        GatewaySignatureClientOptions options,
        string apiKeyIdClaim,
        string tenantIdClaim)
    {
        var claims = CreateClaims(name, options, apiKeyIdClaim, tenantIdClaim);
        var keys = LoadKeys(options.PublicKeyFile!);

        return new GatewaySignatureClient(claims, keys);
    }

    /// <summary>
    /// Whether any of the client's public keys verifies the signature.
    /// </summary>
    /// <param name="data">The signed bytes.</param>
    /// <param name="signature">The raw signature.</param>
    /// <param name="padding">RSA padding of the configured algorithm.</param>
    /// <returns>True when a key verifies the signature.</returns>
    public bool Verify(byte[] data, ReadOnlySpan<byte> signature, RSASignaturePadding padding)
    {
        foreach (var key in _keys)
        {
            if (Verify(key, data, signature, padding))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates the identity this client authenticates as.
    /// </summary>
    /// <returns>The client principal.</returns>
    public ClaimsPrincipal CreatePrincipal()
        => new(new ClaimsIdentity(
            _claims,
            GatewaySignatureAuthenticationHandler.SchemeName,
            SubjectClaim,
            RoleClaim));

    /// <summary>
    /// Verifies with one key. An RSA instance is not thread-safe, so concurrent requests take turns.
    /// </summary>
    private static bool Verify(RSA key, byte[] data, ReadOnlySpan<byte> signature, RSASignaturePadding padding)
    {
        lock (key)
        {
            try
            {
                return key.VerifyData(data, signature, HashAlgorithmName.SHA256, padding);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Builds the claims of a client: subject, client id, issuer, scopes, roles and tenant.
    /// </summary>
    private static Claim[] CreateClaims(
        string name,
        GatewaySignatureClientOptions options,
        string apiKeyIdClaim,
        string tenantIdClaim)
    {
        List<Claim> claims =
        [
            new(SubjectClaim, name),
            new(apiKeyIdClaim, name),
            new(IssuerClaim, GatewaySignatureAuthenticationHandler.Issuer)
        ];

        if (options.Scopes.Length > 0)
        {
            claims.Add(new Claim(ScopeClaim, string.Join(' ', options.Scopes)));
        }

        claims.AddRange(options.Roles.Select(role => new Claim(RoleClaim, role)));

        if (!string.IsNullOrWhiteSpace(options.TenantId))
        {
            claims.Add(new Claim(tenantIdClaim, options.TenantId));
        }

        return [.. claims];
    }

    /// <summary>
    /// Loads the public keys of a PEM file as RSA instances ready to verify signatures.
    /// </summary>
    private static RSA[] LoadKeys(string publicKeyFile)
        => [.. GatewayPublicKeyLoader.Load(publicKeyFile)
            .Cast<RsaSecurityKey>()
            .Select(key => RSA.Create(key.Parameters))];
}
