using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Authentication.Keys;

internal static class GatewayPublicKeyLoader
{
    private const int MinimumRsaKeySize = 2048;

    /// <summary>
    /// Reads public keys from a JWKS or PEM file; rejects private keys and RSA keys under 2048 bits.
    /// </summary>
    /// <param name="path">The key file.</param>
    /// <returns>The keys.</returns>
    /// <exception cref="InvalidDataException">The file is not valid, holds a private key, or the key is too weak.</exception>
    public static IReadOnlyList<SecurityKey> Load(string path)
    {
        var content = File.ReadAllText(path);
        var keys = new List<SecurityKey>();

        try
        {
            if (content.AsSpan().TrimStart().StartsWith("{"))
            {
                foreach (var jwk in new JsonWebKeySet(content).Keys)
                {
                    if (jwk.Use is not (null or "sig"))
                    {
                        continue;
                    }

                    if (jwk.HasPrivateKey || jwk.D is not null)
                    {
                        throw new InvalidDataException("JWKS must contain public keys only.");
                    }

                    switch (jwk.Kty)
                    {
                        case "RSA":
                            using (var rsa = RSA.Create(new RSAParameters
                            {
                                Modulus = Base64UrlEncoder.DecodeBytes(jwk.N),
                                Exponent = Base64UrlEncoder.DecodeBytes(jwk.E)
                            }))
                            {
                                keys.Add(ToSecurityKey(rsa, jwk.Kid));
                            }

                            break;

                        case "EC":
                            keys.Add(new ECDsaSecurityKey(ECDsa.Create(new ECParameters
                            {
                                Curve = jwk.Crv switch
                                {
                                    "P-256" => ECCurve.NamedCurves.nistP256,
                                    "P-384" => ECCurve.NamedCurves.nistP384,
                                    "P-521" => ECCurve.NamedCurves.nistP521,
                                    _ => throw new InvalidDataException("Unsupported EC curve. Use P-256, P-384 or P-521.")
                                },
                                Q = new ECPoint
                                {
                                    X = Base64UrlEncoder.DecodeBytes(jwk.X),
                                    Y = Base64UrlEncoder.DecodeBytes(jwk.Y)
                                }
                            }))
                            {
                                KeyId = jwk.Kid
                            });
                            break;

                        default:
                            throw new InvalidDataException("JWKS keys must be RSA or EC public keys.");
                    }
                }
            }
            else
            {
                var remaining = content.AsSpan();
                while (PemEncoding.TryFind(remaining, out var fields))
                {
                    var label = remaining[fields.Label].ToString();
                    var der = Convert.FromBase64String(remaining[fields.Base64Data].ToString());

                    switch (label)
                    {
                        case "PUBLIC KEY":
                            using (var rsa = RSA.Create())
                            {
                                try
                                {
                                    rsa.ImportSubjectPublicKeyInfo(der, out _);
                                    keys.Add(ToSecurityKey(rsa));
                                    break;
                                }
                                catch (CryptographicException)
                                {
                                    // Not an RSA key; try EC below.
                                }
                            }

                            var ecdsa = ECDsa.Create();
                            try
                            {
                                ecdsa.ImportSubjectPublicKeyInfo(der, out _);
                                keys.Add(new ECDsaSecurityKey(ecdsa));
                            }
                            catch
                            {
                                ecdsa.Dispose();
                                throw;
                            }

                            break;

                        case "RSA PUBLIC KEY":
                            using (var rsa = RSA.Create())
                            {
                                rsa.ImportRSAPublicKey(der, out _);
                                keys.Add(ToSecurityKey(rsa));
                            }

                            break;

                        case "CERTIFICATE":
                            using (var certificate = X509CertificateLoader.LoadCertificate(der))
                            {
                                using var certificateRsa = certificate.GetRSAPublicKey();
                                if (certificateRsa is not null)
                                {
                                    keys.Add(ToSecurityKey(certificateRsa));
                                    break;
                                }

                                using var certificateEc = certificate.GetECDsaPublicKey()
                                    ?? throw new InvalidDataException("The certificate contains neither an RSA nor an EC public key.");
                                keys.Add(new ECDsaSecurityKey(ECDsa.Create(certificateEc.ExportParameters(false))));
                            }

                            break;

                        default:
                            throw new InvalidDataException(label.Contains("PRIVATE KEY", StringComparison.Ordinal)
                                ? "JWT verification requires a public key, not a private key."
                                : "Unsupported PEM block. Use PUBLIC KEY, RSA PUBLIC KEY or CERTIFICATE.");
                    }

                    remaining = remaining[fields.Location.End..];
                }
            }

            return keys.Count > 0 ? keys : throw new InvalidDataException("The file contains no usable public keys.");
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or CryptographicException
                or FormatException
                or JsonException
                or InvalidOperationException)
        {
            throw new InvalidDataException("The file is not a valid JWKS or PEM public key.", exception);
        }
    }

    /// <summary>
    /// Copies an RSA public key into a security key after checking its size; the caller owns <paramref name="rsa"/>.
    /// </summary>
    /// <param name="rsa">The RSA key.</param>
    /// <param name="keyId">Optional key id.</param>
    /// <returns>The security key.</returns>
    /// <exception cref="InvalidDataException">The key is smaller than 2048 bits.</exception>
    private static RsaSecurityKey ToSecurityKey(RSA rsa, string? keyId = null)
    {
        return rsa.KeySize < MinimumRsaKeySize
            ? throw new InvalidDataException($"RSA public keys must be at least {MinimumRsaKeySize} bits.")
            : new RsaSecurityKey(rsa.ExportParameters(false)) {
                KeyId = keyId
            };
    }
}
