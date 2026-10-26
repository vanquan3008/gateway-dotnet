using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Core.Gateway.Api.Authentication.Keys;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Core.Gateway.Api.Tests.Authentication.Keys;

public sealed class GatewayPublicKeyLoaderTests : IDisposable
{
    private readonly List<string> _files = [];

    [Theory]
    [InlineData("spki-rsa", typeof(RsaSecurityKey))]
    [InlineData("pkcs1-rsa", typeof(RsaSecurityKey))]
    [InlineData("certificate-rsa", typeof(RsaSecurityKey))]
    [InlineData("jwks-rsa", typeof(RsaSecurityKey))]
    [InlineData("spki-ec", typeof(ECDsaSecurityKey))]
    [InlineData("certificate-ec", typeof(ECDsaSecurityKey))]
    [InlineData("jwks-ec", typeof(ECDsaSecurityKey))]
    public void Load_DetectsFormatFromContent(string format, Type keyType)
    {
        var keys = GatewayPublicKeyLoader.Load(Write(Sample(format)));

        Assert.IsType(keyType, Assert.Single(keys));
    }

    [Fact]
    public void Load_ReturnsEveryJwksKeyWithItsKeyId()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var jwks = $$"""{"keys":[{{RsaJwk(first, "2025-key")}},{{RsaJwk(second, "2026-key")}}]}""";

        var keys = GatewayPublicKeyLoader.Load(Write(jwks));

        Assert.Equal(["2025-key", "2026-key"], keys.Select(key => key.KeyId));
    }

    [Theory]
    [InlineData("private-pem")]
    [InlineData("rsa-1024")]
    [InlineData("jwks-private")]
    [InlineData("jwks-symmetric")]
    [InlineData("jwks-encryption-only")]
    [InlineData("garbage")]
    public void Load_RejectsUnsafeOrUnusableKeys(string format)
    {
        var path = Write(Sample(format));

        var exception = Assert.Throws<InvalidDataException>(() => GatewayPublicKeyLoader.Load(path));

        Assert.DoesNotContain("PRIVATE KEY-----", exception.Message);
    }

    public void Dispose() => _files.ForEach(File.Delete);

    private string Write(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-keys-{Guid.NewGuid():N}");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    private static string Sample(string format)
    {
        using var rsa = RSA.Create(format == "rsa-1024" ? 1024 : 2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return format switch
        {
            "spki-rsa" or "rsa-1024" => rsa.ExportSubjectPublicKeyInfoPem(),
            "pkcs1-rsa" => rsa.ExportRSAPublicKeyPem(),
            "certificate-rsa" => new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).ExportCertificatePem(),
            "jwks-rsa" => $$"""{"keys":[{{RsaJwk(rsa, "k1")}}]}""",
            "spki-ec" => ec.ExportSubjectPublicKeyInfoPem(),
            "certificate-ec" => new CertificateRequest("CN=test", ec, HashAlgorithmName.SHA256)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).ExportCertificatePem(),
            "jwks-ec" => $$"""{"keys":[{{EcJwk(ec)}}]}""",
            "private-pem" => rsa.ExportPkcs8PrivateKeyPem(),
            "jwks-private" => $$"""{"keys":[{{RsaJwk(rsa, "k1", includePrivate: true)}}]}""",
            "jwks-symmetric" => """{"keys":[{"kty":"oct","kid":"k1","k":"c2VjcmV0LXNlY3JldC1zZWNyZXQtc2VjcmV0LXNlY3JldA"}]}""",
            "jwks-encryption-only" => $$"""{"keys":[{{RsaJwk(rsa, "k1", use: "enc")}}]}""",
            "garbage" => "not a key",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static string RsaJwk(RSA rsa, string keyId, bool includePrivate = false, string use = "sig")
    {
        var p = rsa.ExportParameters(includePrivate);
        var jwk = $$"""{"kty":"RSA","kid":"{{keyId}}","use":"{{use}}","n":"{{Base64UrlEncoder.Encode(p.Modulus)}}","e":"{{Base64UrlEncoder.Encode(p.Exponent)}}"}""";
        return includePrivate ? jwk[..^1] + $$""","d":"{{Base64UrlEncoder.Encode(p.D)}}"}""" : jwk;
    }

    private static string EcJwk(ECDsa ec)
    {
        var p = ec.ExportParameters(false);
        return $$"""{"kty":"EC","kid":"ec-key","use":"sig","crv":"P-256","x":"{{Base64UrlEncoder.Encode(p.Q.X)}}","y":"{{Base64UrlEncoder.Encode(p.Q.Y)}}"}""";
    }
}
