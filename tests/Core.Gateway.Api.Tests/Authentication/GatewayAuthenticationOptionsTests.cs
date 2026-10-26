using System.Security.Cryptography;
using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Authentication.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Authentication;

public sealed class GatewayAuthenticationOptionsTests
{
    [Fact]
    public async Task AddGatewayAuthentication_RequiresIdentityWhenEnabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true",
                ["Authentication:Jwt:Authority"] = "https://identity.example.com",
                ["Authentication:Jwt:Audience"] = "gateway"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: false)
            .BuildServiceProvider();

        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallbackPolicy = await policyProvider.GetFallbackPolicyAsync();

        Assert.NotNull(fallbackPolicy);
        Assert.Contains(
            fallbackPolicy.Requirements,
            requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void AddGatewayAuthentication_RejectsDisabledProductionAuthentication()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "false"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: true)
            .BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GatewayAuthenticationOptions>>().Value);

        Assert.Contains("enabled in Production", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddGatewayAuthentication_AllowsDisabledAuthenticationOutsideProduction()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Enabled"] = "false" })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: false)
            .BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<GatewayAuthenticationOptions>>().Value.Enabled);
    }

    [Fact]
    public void Authority_RestrictsTokensToAsymmetricAlgorithmsAndRawClaimNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Enabled"] = "true",
                ["Authentication:Jwt:Authority"] = "https://identity.example.com",
                ["Authentication:Jwt:Audience"] = "gateway"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: true)
            .BuildServiceProvider();

        var validation = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;

        Assert.NotNull(validation.ValidAlgorithms);
        Assert.All(validation.ValidAlgorithms, algorithm => Assert.Matches("^(RS|PS|ES)(256|384|512)$", algorithm));
        Assert.True(validation.RequireSignedTokens);
        Assert.Equal("sub", validation.NameClaimType);
        Assert.Equal("role", validation.RoleClaimType);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("30001")]
    public void AddGatewayAuthentication_RejectsInvalidBackchannelTimeout(string timeout)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Enabled"] = "true",
            ["Authentication:Jwt:Authority"] = "https://identity.example.com",
            ["Authentication:Jwt:Audience"] = "gateway",
            ["Authentication:Jwt:BackchannelTimeoutMilliseconds"] = timeout
        }).Build();
        using var provider = new ServiceCollection().AddLogging()
            .AddGatewayAuthentication(configuration, isProduction: false).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value);
    }

    [Fact]
    public void Providers_StaticKeyWorksInProductionWithoutAuthority()
    {
        var options = ProviderOptions(ProviderConfiguration());
        Assert.Single(options.Providers);
        Assert.Null(options.Authority);
    }

    [Theory]
    [InlineData("Issuer", "", "Issuer is required")]
    [InlineData("Audience", "", "Audience is required")]
    [InlineData("Enabled", "false", "at least one enabled provider")]
    [InlineData("SigningKey", "", "exactly one")]
    [InlineData("SigningKey", "short", "must be at least")]
    [InlineData("Authority", "https://identity.test", "exactly one")]
    [InlineData("ValidAlgorithms:0", "", "ValidAlgorithms")]
    [InlineData("ValidAlgorithms:0", "none", "ValidAlgorithms")]
    [InlineData("ValidAlgorithms:0", "RS256", "ValidAlgorithms")]
    [InlineData("ValidAlgorithms:0", "HS512", "must be at least")]
    [InlineData("RoleClaimType", "", "RoleClaimType")]
    public void Providers_RejectInvalidConfiguration(string setting, string value, string error)
    {
        var values = ProviderConfiguration();
        values[$"Authentication:Jwt:Providers:KAIVA:{setting}"] = value;
        var exception = Assert.Throws<OptionsValidationException>(() => ProviderOptions(values));
        Assert.Contains(error, exception.Message);
        Assert.DoesNotContain(values["Authentication:Jwt:Providers:KAIVA:SigningKey"] is { Length: > 20 } secret ? secret : "unused-secret", exception.Message);
    }

    [Fact]
    public void Providers_RejectDuplicateIssuers()
    {
        var values = ProviderConfiguration();
        foreach (var pair in values.Where(pair => pair.Key.Contains(":KAIVA:")).ToArray())
            values[pair.Key.Replace(":KAIVA:", ":KChat:")] = pair.Value;
        var exception = Assert.Throws<OptionsValidationException>(() => ProviderOptions(values));
        Assert.Contains("Issuer must be unique", exception.Message);
    }

    [Theory]
    [InlineData("http://identity.test", true)]
    [InlineData("file:///identity", false)]
    public void Providers_RejectInvalidAuthority(string authority, bool production)
    {
        var values = ProviderConfiguration();
        values["Authentication:Jwt:Providers:KAIVA:SigningKey"] = "";
        values["Authentication:Jwt:Providers:KAIVA:Authority"] = authority;
        values["Authentication:Jwt:Providers:KAIVA:ValidAlgorithms:0"] = "RS256";
        Assert.Throws<OptionsValidationException>(() => ProviderOptions(values, production));
    }

    [Theory]
    [InlineData(false, 2048, true)]
    [InlineData(true, 2048, false)]
    [InlineData(false, 1024, false)]
    public void Providers_RequireStrongRsaPublicKey(bool privateKey, int bits, bool valid)
    {
        using var rsa = RSA.Create(bits);
        using var file = new TempPemFile(privateKey ? rsa.ExportPkcs8PrivateKeyPem() : rsa.ExportSubjectPublicKeyInfoPem());
        var values = ProviderConfiguration();
        values["Authentication:Jwt:Providers:KAIVA:SigningKey"] = "";
        values["Authentication:Jwt:Providers:KAIVA:PublicKeyFile"] = file.Path;
        values["Authentication:Jwt:Providers:KAIVA:ValidAlgorithms:0"] = "RS256";
        if (valid)
            Assert.Single(ProviderOptions(values).Providers);
        else
            Assert.Throws<OptionsValidationException>(() => ProviderOptions(values));
    }

    [Fact]
    public void Providers_ReadPublicKeyFromFile()
    {
        using var rsa = RSA.Create(2048);
        using var file = new TempPemFile(rsa.ExportSubjectPublicKeyInfoPem());
        var values = ProviderConfiguration();
        values["Authentication:Jwt:Providers:KAIVA:SigningKey"] = "";
        values["Authentication:Jwt:Providers:KAIVA:PublicKeyFile"] = file.Path;
        values["Authentication:Jwt:Providers:KAIVA:ValidAlgorithms:0"] = "RS256";
        Assert.Single(ProviderOptions(values).Providers);
    }

    [Fact]
    public void Providers_RejectMissingPublicKeyFile_NamingThePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gateway-missing-{Guid.NewGuid():N}.pem");
        var values = ProviderConfiguration();
        values["Authentication:Jwt:Providers:KAIVA:SigningKey"] = "";
        values["Authentication:Jwt:Providers:KAIVA:PublicKeyFile"] = path;
        values["Authentication:Jwt:Providers:KAIVA:ValidAlgorithms:0"] = "RS256";
        var exception = Assert.Throws<OptionsValidationException>(() => ProviderOptions(values));
        Assert.Contains($"PublicKeyFile '{path}' cannot be read", exception.Message);
    }

    [Theory]
    [InlineData("ES256", true)]
    [InlineData("RS256", false)]
    public void Providers_MatchAlgorithmsToTheKeyTypeInTheFile(string algorithm, bool valid)
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var file = new TempPemFile(ec.ExportSubjectPublicKeyInfoPem());
        var values = ProviderConfiguration();
        values["Authentication:Jwt:Providers:KAIVA:SigningKey"] = "";
        values["Authentication:Jwt:Providers:KAIVA:PublicKeyFile"] = file.Path;
        values["Authentication:Jwt:Providers:KAIVA:ValidAlgorithms:0"] = algorithm;
        if (valid)
            Assert.Single(ProviderOptions(values).Providers);
        else
            Assert.Contains("ValidAlgorithms", Assert.Throws<OptionsValidationException>(() => ProviderOptions(values)).Message);
    }

    [Fact]
    public void Providers_RejectSigningKeyAndPublicKeyFileTogether()
    {
        var values = ProviderConfiguration();
        values["Authentication:Jwt:Providers:KAIVA:PublicKeyFile"] = "/keys/kaiva.pem";
        var exception = Assert.Throws<OptionsValidationException>(() => ProviderOptions(values));
        Assert.Contains("exactly one", exception.Message);
    }

    private sealed class TempPemFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gateway-test-{Guid.NewGuid():N}.pem");

        public TempPemFile(string content) => File.WriteAllText(Path, content);

        public void Dispose() => File.Delete(Path);
    }

    private static Dictionary<string, string?> ProviderConfiguration() => new()
    {
        ["Authentication:Enabled"] = "true",
        ["Authentication:Jwt:Providers:KAIVA:Issuer"] = "https://kaiva.test",
        ["Authentication:Jwt:Providers:KAIVA:Audience"] = "gateway-api",
        ["Authentication:Jwt:Providers:KAIVA:SigningKey"] = "test-only-kaiva-secret-32-bytes-long",
        ["Authentication:Jwt:Providers:KAIVA:ValidAlgorithms:0"] = "HS256"
    };

    private static GatewayJwtOptions ProviderOptions(Dictionary<string, string?> values, bool production = true)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddLogging()
            .AddGatewayAuthentication(configuration, production).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value;
    }
}
