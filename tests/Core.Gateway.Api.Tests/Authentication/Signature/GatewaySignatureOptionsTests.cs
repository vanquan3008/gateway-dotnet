using System.Security.Cryptography;
using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Authentication.ApiKey;
using Core.Gateway.Api.Authentication.Jwt;
using Core.Gateway.Api.Authentication.Signature;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Authentication.Signature;

public sealed class GatewaySignatureOptionsTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void Signature_WorksWithoutAnyJwtConfiguration()
    {
        var values = Settings(WriteRsaKey());

        var provider = Build(values, production: true);

        Assert.Single(provider.GetRequiredService<IOptions<GatewaySignatureOptions>>().Value.Clients);
        Assert.Empty(provider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value.Providers);
    }

    [Fact]
    public void Signature_StillRejectsAHalfConfiguredJwt()
    {
        var values = Settings(WriteRsaKey());
        values["Authentication:Jwt:Audience"] = "gateway-api";

        var provider = Build(values, production: true);

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value);
        Assert.Contains("Authority", exception.Message);
    }

    [Fact]
    public void ApiKeyAlone_AlsoMakesJwtOptional()
    {
        var values = new Dictionary<string, string?>
        {
            ["Authentication:Enabled"] = "true",
            ["Authentication:ApiKeys:Enabled"] = "true",
            ["Authentication:ApiKeys:Clients:billing:KeyHash"] = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
        };

        var provider = Build(values, production: true);

        Assert.Single(provider.GetRequiredService<IOptions<GatewayApiKeyOptions>>().Value.Clients);
        _ = provider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value;
    }

    [Fact]
    public void Signature_IgnoresItsSettingsWhileAuthenticationIsDisabled()
    {
        var values = Settings("/missing.pem");
        values["Authentication:Enabled"] = "false";

        var provider = Build(values, production: false);

        _ = provider.GetRequiredService<IOptions<GatewaySignatureOptions>>().Value;
    }

    [Theory]
    [InlineData("Algorithm", "ES256", "RS256 or PS256")]
    [InlineData("SignatureHeader", "Authorization", "custom header names")]
    [InlineData("TimestampHeader", "X-Signature", "must be different")]
    [InlineData("ClientIdHeader", "X Client", "custom header names")]
    [InlineData("MaxClockSkewSeconds", "0", "MaxClockSkewSeconds")]
    public void Signature_RejectsInvalidSettings(string setting, string value, string error)
    {
        var values = Settings(WriteRsaKey());
        values[$"Authentication:Signature:{setting}"] = value;

        var exception = Assert.Throws<OptionsValidationException>(() => Build(values).GetRequiredService<IOptions<GatewaySignatureOptions>>().Value);

        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public void Signature_RejectsAnEcKey()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var values = Settings(WriteFile(ec.ExportSubjectPublicKeyInfoPem()));

        var exception = Assert.Throws<OptionsValidationException>(() => Build(values).GetRequiredService<IOptions<GatewaySignatureOptions>>().Value);

        Assert.Contains("RSA public keys only", exception.Message);
    }

    [Fact]
    public void Signature_RejectsAPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var values = Settings(WriteFile(rsa.ExportPkcs8PrivateKeyPem()));

        var exception = Assert.Throws<OptionsValidationException>(() => Build(values).GetRequiredService<IOptions<GatewaySignatureOptions>>().Value);

        Assert.Contains("private key", exception.Message);
    }

    [Fact]
    public void Signature_NamesAMissingKeyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-missing-{Guid.NewGuid():N}.pem");
        var values = Settings(path);

        var exception = Assert.Throws<OptionsValidationException>(() => Build(values).GetRequiredService<IOptions<GatewaySignatureOptions>>().Value);

        Assert.Contains($"PublicKeyFile '{path}' cannot be read", exception.Message);
    }

    [Theory]
    [InlineData("Clients:partner:PublicKeyFile", "", "PublicKeyFile is required")]
    [InlineData("Clients:partner:Enabled", "false", "at least one enabled client")]
    [InlineData("Clients:partner:Scopes:0", "orders write", "without spaces")]
    public void Signature_RejectsInvalidClients(string setting, string value, string error)
    {
        var values = Settings(WriteRsaKey());
        values[$"Authentication:Signature:{setting}"] = value;

        var exception = Assert.Throws<OptionsValidationException>(() => Build(values).GetRequiredService<IOptions<GatewaySignatureOptions>>().Value);

        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public void Signature_RejectsAnUnsafeClientName()
    {
        var values = Settings(WriteRsaKey());
        values["Authentication:Signature:Clients:bad name:PublicKeyFile"] = values["Authentication:Signature:Clients:partner:PublicKeyFile"];

        var exception = Assert.Throws<OptionsValidationException>(() => Build(values).GetRequiredService<IOptions<GatewaySignatureOptions>>().Value);

        Assert.Contains("Client name must be", exception.Message);
    }

    public void Dispose() => _files.ForEach(File.Delete);

    private string WriteRsaKey()
    {
        using var rsa = RSA.Create(2048);
        return WriteFile(rsa.ExportSubjectPublicKeyInfoPem());
    }

    private string WriteFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-signature-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    private static Dictionary<string, string?> Settings(string publicKeyFile) => new()
    {
        ["Authentication:Enabled"] = "true",
        ["Authentication:Signature:Enabled"] = "true",
        ["Redis:ConnectionString"] = "redis.example.com:6379",
        ["Authentication:Signature:Clients:partner:PublicKeyFile"] = publicKeyFile
    };

    private static ServiceProvider Build(Dictionary<string, string?> values, bool production = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ServiceCollection().AddLogging()
            .AddGatewayAuthentication(configuration, production).BuildServiceProvider();
    }
}
