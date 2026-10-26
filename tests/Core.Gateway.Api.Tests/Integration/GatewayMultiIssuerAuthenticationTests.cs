using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Store;
using Core.Gateway.Api.RateLimiting.Distributed.Store.Interfaces;
using Core.Gateway.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    [Fact]
    public async Task MultipleIssuers_ValidateSsoHmacAndRsaAndPreserveBearerForBackend()
    {
        using var tokens = new MultiProviderTokens();
        using var backchannel = new TestOidcBackchannel();
        await using var host = await GatewayTestHost.StartAsync(tokens.Settings, backchannel.Configure,
            authenticationScheme: GatewayAuthenticationExtensions.ProviderSchemePrefix + "SSO",
            backendProviders: tokens.BackendProviders);
        tokens.BackendProviders["https://identity.test"] = BackendValidation("https://identity.test", host.BackendSigningKey);

        foreach (var token in new[] { host.CreateToken(), tokens.CreateToken("KAIVA"), tokens.CreateToken("KChat") })
        {
            using var response = await host.Client.SendAsync(TokenRequest(token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var echo = (await response.Content.ReadFromJsonAsync<ProxyEcho>())!;
            Assert.Equal("Bearer " + token, echo.Authorization);
        }

        Assert.Equal(3, host.DownstreamRequests);
        Assert.Equal(2, backchannel.Calls);
    }

    [Theory]
    [InlineData("KAIVA", "audience")]
    [InlineData("KChat", "audience")]
    [InlineData("KAIVA", "audience-slash")]
    [InlineData("KAIVA", "expired")]
    [InlineData("KChat", "expired")]
    [InlineData("KAIVA", "future")]
    [InlineData("KChat", "future")]
    [InlineData("KAIVA", "signature")]
    [InlineData("KChat", "signature")]
    [InlineData("KAIVA", "algorithm")]
    [InlineData("KChat", "algorithm")]
    [InlineData("KAIVA", "issuer")]
    [InlineData("KAIVA", "other-provider")]
    [InlineData("KChat", "other-provider")]
    [InlineData("KAIVA", "unsigned")]
    [InlineData("KChat", "unsigned")]
    [InlineData("KAIVA", "no-expiration")]
    [InlineData("KChat", "no-expiration")]
    public async Task MultipleIssuers_RejectInvalidTokensWithoutCallingSso(string provider, string failure)
    {
        using var tokens = new MultiProviderTokens();
        using var backchannel = new TestOidcBackchannel { Unavailable = true };
        await using var host = await GatewayTestHost.StartAsync(tokens.Settings, backchannel.Configure,
            authenticationScheme: GatewayAuthenticationExtensions.ProviderSchemePrefix + "SSO");

        using var response = await host.Client.SendAsync(TokenRequest(tokens.CreateToken(provider, failure)));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
        Assert.Equal(0, host.DownstreamRequests);
        Assert.Equal(0, backchannel.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.bm90LWpzb24.c2lnbmF0dXJl")]
    [InlineData("eyJhbGciOiJub25lIn0.e30.")]
    public async Task MultipleIssuers_RejectMissingMalformedAndUnknownIssuerTokens(string? token)
    {
        using var tokens = new MultiProviderTokens();
        using var backchannel = new TestOidcBackchannel { Unavailable = true };
        await using var host = await GatewayTestHost.StartAsync(tokens.Settings, backchannel.Configure,
            authenticationScheme: GatewayAuthenticationExtensions.ProviderSchemePrefix + "SSO");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.DownstreamRequests);
        Assert.Equal(0, backchannel.Calls);
    }

    [Fact]
    public async Task MultipleIssuers_SsoOutageDoesNotBlockStaticKeysOrHealth()
    {
        using var tokens = new MultiProviderTokens();
        using var backchannel = new TestOidcBackchannel { Unavailable = true };
        await using var host = await GatewayTestHost.StartAsync(tokens.Settings, backchannel.Configure,
            authenticationScheme: GatewayAuthenticationExtensions.ProviderSchemePrefix + "SSO",
            backendProviders: tokens.BackendProviders);

        using var failed = await host.Client.SendAsync(TokenRequest(host.CreateToken()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var calls = backchannel.Calls;
        foreach (var provider in new[] { "KAIVA", "KChat" })
        {
            using var response = await host.Client.SendAsync(TokenRequest(tokens.CreateToken(provider)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await host.Client.SendAsync(TokenRequest(host.CreateToken(), path));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        Assert.Equal(calls, backchannel.Calls);
        Assert.Equal(2, host.DownstreamRequests);
    }

    [Fact]
    public async Task MultipleIssuers_DisabledProviderCannotAuthenticate()
    {
        using var tokens = new MultiProviderTokens();
        tokens.Settings["Authentication:Jwt:Providers:KAIVA:Enabled"] = "false";
        await using var host = await GatewayTestHost.StartAsync(tokens.Settings,
            authenticationScheme: GatewayAuthenticationExtensions.ProviderSchemePrefix + "SSO");
        using var response = await host.Client.SendAsync(TokenRequest(tokens.CreateToken("KAIVA")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.DownstreamRequests);
    }

    [Fact]
    public async Task MultipleIssuers_HmacKeysRemainIsolatedWhenAlgorithmsAndAudiencesMatch()
    {
        using var tokens = new MultiProviderTokens();
        const string kchatSecret = "test-only-kchat-independent-secret-32-bytes-long";
        tokens.Settings["Authentication:Jwt:Providers:KChat:PublicKeyFile"] = "";
        tokens.Settings["Authentication:Jwt:Providers:KChat:SigningKey"] = kchatSecret;
        tokens.Settings["Authentication:Jwt:Providers:KChat:ValidAlgorithms:0"] = "HS256";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(kchatSecret));
        tokens.BackendProviders["https://kchat.test"] = BackendValidation("https://kchat.test", key);
        await using var host = await GatewayTestHost.StartAsync(tokens.Settings,
            authenticationScheme: GatewayAuthenticationExtensions.ProviderSchemePrefix + "SSO",
            backendProviders: tokens.BackendProviders);

        using var forged = await host.Client.SendAsync(TokenRequest(tokens.CreateToken("KAIVA", "other-provider")));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        var jwt = new JwtSecurityToken("https://kchat.test", "gateway-api", [new Claim("sub", "user-a")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, "HS256"));
        using var valid = await host.Client.SendAsync(TokenRequest(new JwtSecurityTokenHandler().WriteToken(jwt)));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(1, host.DownstreamRequests);
    }

    [Fact]
    public async Task MultipleIssuers_SeparateUserTenantAndApiKeyQuotasByProvider()
    {
        using var tokens = new MultiProviderTokens();
        tokens.Settings["RateLimiting:Distributed:Enabled"] = "true";
        tokens.Settings["RateLimiting:Distributed:Authenticated:PermitLimit"] = "1";
        tokens.Settings["RateLimiting:Distributed:Tenant:PermitLimit"] = "1";
        tokens.Settings["RateLimiting:Distributed:ApiKey:PermitLimit"] = "1";
        tokens.Settings["Authentication:Enabled"] = "true";
        var store = new ProviderQuotaStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(tokens.Settings);
        builder.Services.AddGatewayAuthentication(builder.Configuration, isProduction: true)
            .AddGatewayRateLimiting(builder.Configuration)
            .AddSingleton<IDistributedRateLimitStore>(store);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<DistributedRateLimitingMiddleware>();
        app.UseAuthorization();
        var accepted = 0;
        app.MapGet("/api/users", () => { accepted++; return "OK"; });
        await app.StartAsync();
        using var client = app.GetTestClient();

        foreach (var provider in new[] { "KAIVA", "KChat" })
        {
            using var allowed = await client.SendAsync(TokenRequest(tokens.CreateToken(provider)));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            using var userRejected = await client.SendAsync(TokenRequest(tokens.CreateToken(provider)));
            Assert.Equal(HttpStatusCode.TooManyRequests, userRejected.StatusCode);
            using var tenantRejected = await client.SendAsync(TokenRequest(tokens.CreateToken(provider, user: "user-b")));
            Assert.Equal(HttpStatusCode.TooManyRequests, tenantRejected.StatusCode);
            using var keyRejected = await client.SendAsync(TokenRequest(tokens.CreateToken(provider, user: "user-c", tenant: "tenant-b")));
            Assert.Equal(HttpStatusCode.TooManyRequests, keyRejected.StatusCode);
        }
        Assert.Equal(2, accepted);
    }

    private static HttpRequestMessage TokenRequest(string token, string path = "/api/users") => new(HttpMethod.Get, path)
    {
        Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
    };

    private static TokenValidationParameters BackendValidation(string issuer, SecurityKey key) => new()
    {
        ValidIssuer = issuer,
        ValidAudience = "gateway-api",
        IssuerSigningKey = key,
        ClockSkew = TimeSpan.Zero
    };

    private sealed class MultiProviderTokens : IDisposable
    {
        private const string Secret = "test-only-kaiva-secret-at-least-64-bytes-long-for-hs512-algorithm-check";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly SymmetricSecurityKey _hmac = new(Encoding.UTF8.GetBytes(Secret));
        private readonly string _publicKeyFile = Path.Combine(Path.GetTempPath(), $"gateway-kchat-{Guid.NewGuid():N}.pem");
        public Dictionary<string, string?> Settings { get; } = new()
        {
            ["Authentication:Jwt:Providers:SSO:Issuer"] = "https://identity.test",
            ["Authentication:Jwt:Providers:SSO:Authority"] = "https://identity.test",
            ["Authentication:Jwt:Providers:SSO:Audience"] = "gateway-api",
            ["Authentication:Jwt:Providers:SSO:ValidAlgorithms:0"] = "RS256",
            ["Authentication:Jwt:Providers:KAIVA:Issuer"] = "https://kaiva.test",
            ["Authentication:Jwt:Providers:KAIVA:Audience"] = "gateway-api",
            ["Authentication:Jwt:Providers:KAIVA:SigningKey"] = Secret,
            ["Authentication:Jwt:Providers:KAIVA:ValidAlgorithms:0"] = "HS256",
            ["Authentication:Jwt:Providers:KChat:Issuer"] = "https://kchat.test",
            ["Authentication:Jwt:Providers:KChat:Audience"] = "gateway-api",
            ["Authentication:Jwt:Providers:KChat:ValidAlgorithms:0"] = "RS256"
        };
        public Dictionary<string, TokenValidationParameters> BackendProviders { get; } = [];

        public MultiProviderTokens()
        {
            File.WriteAllText(_publicKeyFile, _rsa.ExportSubjectPublicKeyInfoPem());
            Settings["Authentication:Jwt:Providers:KChat:PublicKeyFile"] = _publicKeyFile;
            BackendProviders["https://kaiva.test"] = BackendValidation("https://kaiva.test", _hmac);
            BackendProviders["https://kchat.test"] = BackendValidation("https://kchat.test", new RsaSecurityKey(_rsa));
        }

        public string CreateToken(string provider, string? failure = null, string user = "same-user", string tenant = "same-tenant")
        {
            using var wrongRsa = failure == "signature" && provider == "KChat" ? RSA.Create(2048) : null;
            SecurityKey key = provider == "KAIVA" ? _hmac : new RsaSecurityKey(wrongRsa ?? _rsa);
            if (failure == "signature" && provider == "KAIVA")
                key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
            var issuer = $"https://{provider.ToLowerInvariant()}.test";
            if (failure == "issuer") issuer = "https://unknown.test";
            if (failure == "other-provider") issuer = provider == "KAIVA" ? "https://kchat.test" : "https://kaiva.test";
            var algorithm = provider == "KAIVA" ? "HS256" : "RS256";
            if (failure == "algorithm") algorithm = provider == "KAIVA" ? "HS512" : "RS512";
            var jwt = new JwtSecurityToken(issuer,
                failure == "audience" ? "wrong-api" : failure == "audience-slash" ? "gateway-api/" : "gateway-api",
                [new Claim("sub", user), new Claim("tenant_id", tenant), new Claim("api_key_id", "same-key"), new Claim("role", "member")],
                failure == "no-expiration" ? null : DateTime.UtcNow.AddMinutes(failure == "future" ? 5 : -10),
                failure == "no-expiration" ? null : DateTime.UtcNow.AddMinutes(failure == "expired" ? -1 : 10),
                failure == "unsigned" ? null : new SigningCredentials(key, algorithm));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }

        public void Dispose()
        {
            _rsa.Dispose();
            File.Delete(_publicKeyFile);
        }
    }

    private sealed class ProviderQuotaStore : IDistributedRateLimitStore
    {
        public bool IsConfigured => true;

        private readonly ConcurrentDictionary<string, int> _counts = new();
        public ValueTask<DistributedRateLimitResult> AcquireAsync(string partitionKey, int permitLimit, TimeSpan window, CancellationToken cancellationToken)
        {
            var count = _counts.AddOrUpdate(partitionKey, 1, (_, previous) => previous + 1);
            return ValueTask.FromResult(count <= permitLimit ? DistributedRateLimitResult.Allowed(window) : DistributedRateLimitResult.Rejected(window));
        }
    }
}
