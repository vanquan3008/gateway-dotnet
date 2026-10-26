using System.Security.Cryptography;
using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Authentication.ApiKey;
using Core.Gateway.Api.Authentication.Jwt;
using Core.Gateway.Api.Authentication.Signature;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Core.Gateway.Api.Tests.Authentication;

public sealed class GatewayAuthenticationExamplesTests : IDisposable
{
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"gateway-examples-{Guid.NewGuid():N}.jwks");
    private readonly string _rsaKeyFile = Path.Combine(Path.GetTempPath(), $"gateway-examples-rsa-{Guid.NewGuid():N}.jwks");

    [Theory]
    [InlineData("single-idp.json", true)]
    [InlineData("sso.json", true)]
    [InlineData("partner-hmac.json", true)]
    [InlineData("partner-public-key.json", true)]
    [InlineData("api-key.json", true)]
    [InlineData("combined.json", true)]
    [InlineData("signature.json", true)]
    [InlineData("partner-only.json", true)]
    [InlineData("development-disabled.json", false)]
    public void Example_PassesStartupValidation(string file, bool production)
    {
        File.WriteAllText(_keyFile, Jwks(includeEc: true));
        File.WriteAllText(_rsaKeyFile, Jwks(includeEc: false));
        var path = Path.Combine(AppContext.BaseDirectory, "examples", "authentication", file);
        var example = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var keyFiles = example.AsEnumerable()
            .Where(pair => pair.Key.EndsWith(":PublicKeyFile", StringComparison.Ordinal))

            .ToDictionary(pair => pair.Key, pair => (string?)(pair.Key.Contains(":Signature:", StringComparison.Ordinal) ? _rsaKeyFile : _keyFile));
        keyFiles["Redis:ConnectionString"] = "redis.example.com:6379";
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).AddInMemoryCollection(keyFiles).Build();

        using var provider = new ServiceCollection().AddLogging()
            .AddGatewayAuthentication(configuration, production).BuildServiceProvider();

        _ = provider.GetRequiredService<IOptions<GatewayAuthenticationOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<GatewayApiKeyOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<GatewaySignatureOptions>>().Value;
    }

    public void Dispose()
    {
        File.Delete(_keyFile);
        File.Delete(_rsaKeyFile);
    }

    private static string Jwks(bool includeEc)
    {
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var r = rsa.ExportParameters(false);
        var e = ec.ExportParameters(false);
        var rsaKey = $$"""{"kty":"RSA","kid":"rsa","use":"sig","n":"{{Base64UrlEncoder.Encode(r.Modulus)}}","e":"{{Base64UrlEncoder.Encode(r.Exponent)}}"}""";
        var ecKey = $$"""{"kty":"EC","kid":"ec","use":"sig","crv":"P-256","x":"{{Base64UrlEncoder.Encode(e.Q.X)}}","y":"{{Base64UrlEncoder.Encode(e.Q.Y)}}"}""";
        return includeEc ? $$"""{"keys":[{{rsaKey}},{{ecKey}}]}""" : $$"""{"keys":[{{rsaKey}}]}""";
    }
}
