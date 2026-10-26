using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    [Fact]
    public async Task JwtMetadataFailure_Returns503AndHealthIgnoresBearerToken()
    {
        using var backchannel = new TestOidcBackchannel { Unavailable = true };
        await using var host = await GatewayTestHost.StartAsync(configureAuthentication: backchannel.Configure);

        using var failed = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await AssertProblemAsync(failed, 503, requireRetryAfter: false);
        Assert.Equal(0, host.DownstreamRequests);
        var calls = backchannel.Calls;
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, path));
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        Assert.Equal(calls, backchannel.Calls);
    }

    [Fact]
    public async Task JwtMetadataTimeout_IsBoundedAndReleasesConcurrency()
    {
        using var backchannel = new TestOidcBackchannel { BlockRequests = true };
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["Authentication:BackchannelTimeoutMilliseconds"] = "200",
            ["RateLimiting:Local:InstanceConcurrencyLimit"] = "1",
            ["RateLimiting:Local:ServiceConcurrencyLimit"] = "1"
        }, backchannel.Configure);

        var started = Stopwatch.GetTimestamp();
        using var failed = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.Equal(0, host.DownstreamRequests);

        backchannel.BlockRequests = false;
        backchannel.Release.TrySetResult();
        using var recovered = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task JwtRequestDeadline_Returns504AndReleasesConcurrency()
    {
        using var backchannel = new TestOidcBackchannel { BlockRequests = true };
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["Resilience:RequestTimeoutSeconds"] = "1",
            ["RateLimiting:Local:InstanceConcurrencyLimit"] = "1",
            ["RateLimiting:Local:ServiceConcurrencyLimit"] = "1"
        }, backchannel.Configure);
        using var response = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await AssertProblemAsync(response, 504, requireRetryAfter: false);
        Assert.Equal(0, host.DownstreamRequests);

        backchannel.BlockRequests = false;
        backchannel.Release.TrySetResult();
        using var recovered = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Concurrency_RejectsBeforeJwtMetadataAndKeepsHealthAvailable()
    {
        using var backchannel = new TestOidcBackchannel { BlockRequests = true };
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["RateLimiting:Local:InstanceConcurrencyLimit"] = "1",
            ["RateLimiting:Local:ServiceConcurrencyLimit"] = "1"
        }, backchannel.Configure);
        using var request = host.AuthenticatedRequest(HttpMethod.Get, "/api/users");
        var pending = host.Client.SendAsync(request);
        await backchannel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var otherRequest = host.AuthenticatedRequest(HttpMethod.Get, "/api/orders");
            using var rejected = await host.Client.SendAsync(otherRequest);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Null(rejected.Headers.RetryAfter);
            await AssertProblemAsync(rejected, 429, requireRetryAfter: false);
            Assert.Equal(1, backchannel.Calls);
            using var live = await host.Client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally
        {
            backchannel.BlockRequests = false;
            backchannel.Release.TrySetResult();
        }

        using var completed = await pending;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var recovered = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task JwtCachedKeys_KeepValidatingDuringMetadataOutageWithoutAcceptingInvalidTokens()
    {
        using var backchannel = new TestOidcBackchannel();
        await using var host = await GatewayTestHost.StartAsync(configureAuthentication: backchannel.Configure);
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        backchannel.Unavailable = true;
        var options = host.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var manager = Assert.IsType<ConfigurationManager<OpenIdConnectConfiguration>>(options.ConfigurationManager);
        manager.RequestRefresh();
        await backchannel.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cached = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);

        foreach (var failure in new[] { "expired", "signature", "rotation" })
        {
            using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/users");
            invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(failure));
            using var denied = await host.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        Assert.Equal(2, host.DownstreamRequests);
    }

    [Fact]
    public async Task JwtSigningKeyRotation_RefreshesSdkCacheAndValidatesNewKeyAtGatewayAndBackend()
    {
        using var backchannel = new TestOidcBackchannel();
        await using var host = await GatewayTestHost.StartAsync(configureAuthentication: backchannel.Configure);
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "rotated-key" };
        backchannel.SetSigningKey(key);
        host.BackendSigningKey = key;
        var options = host.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        options.ConfigurationManager!.RequestRefresh();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!(await options.ConfigurationManager.GetConfigurationAsync(deadline.Token))
            .SigningKeys.Any(candidate => candidate.KeyId == key.KeyId))
        {
            await Task.Delay(20, deadline.Token);
        }

        var token = new JwtSecurityToken("https://identity.test", "gateway-api", [new Claim("sub", "user-a")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var rotated = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal(2, host.DownstreamRequests);
    }

    private sealed class TestOidcBackchannel : HttpMessageHandler
    {
        private string _keys = "";
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Unavailable { get; set; }
        public bool BlockRequests { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Configure(JwtBearerOptions options, SecurityKey key)
        {
            SetSigningKey((RsaSecurityKey)key);
            options.Configuration = null;
            options.ConfigurationManager = null;
            options.BackchannelHttpHandler = this;
        }

        public void SetSigningKey(RsaSecurityKey key)
        {
            var parameters = ((RsaSecurityKey)key).Rsa.ExportParameters(false);
            _keys = JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = "RSA", kid = key.KeyId, alg = "RS256", use = "sig",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) } }
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            if (BlockRequests) await Release.Task.WaitAsync(cancellationToken);
            if (Unavailable)
            {
                Failed.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var json = request.RequestUri!.AbsolutePath == "/keys" ? _keys :
                JsonSerializer.Serialize(new { issuer = "https://identity.test", jwks_uri = "https://identity.test/keys" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
