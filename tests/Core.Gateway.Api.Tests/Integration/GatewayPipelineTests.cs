using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Core.Gateway.Api.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

[Collection("Redis rate limiting")]
public sealed partial class GatewayPipelineTests
{
    [Theory]
    [InlineData("/api/users", "/api/users")]
    [InlineData("/api/orders", "/api/orders")]
    [InlineData("/api/products", "/api/products")]
    [InlineData("/users/42", "/api/users/42")]
    [InlineData("/orders/42", "/api/orders/42")]
    [InlineData("/products/42", "/api/products/42")]
    public async Task Proxy_PreservesContractAndSanitizesHeaders(string path, string downstreamPath)
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var request = host.AuthenticatedRequest(HttpMethod.Post, path + "?page=2");
        request.Content = new StringContent("request-body");
        request.Headers.Add(GatewayHeaders.UserId, "forged-user");
        request.Headers.Add(GatewayHeaders.TenantId, "forged-tenant");
        request.Headers.Add(GatewayHeaders.AuthIssuer, "https://forged.test");
        request.Headers.Add(GatewayHeaders.MerchantId, "forged-merchant");
        request.Headers.Add(GatewayHeaders.CorrelationId, "request-123");

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = (await response.Content.ReadFromJsonAsync<ProxyEcho>())!;
        Assert.Equal(downstreamPath, echo.Path.TrimEnd('/'));
        Assert.Equal("?page=2", echo.Query);
        Assert.Equal("POST", echo.Method);
        Assert.Equal("request-body", echo.Body);
        Assert.Equal(request.Headers.Authorization!.ToString(), echo.Authorization);

        Assert.Equal("user-a", echo.UserId);
        Assert.Equal("https://identity.test", echo.AuthIssuer);
        Assert.Equal("", echo.TenantId);
        Assert.Equal("", echo.MerchantId);
        Assert.Equal("request-123", echo.CorrelationId);
        Assert.Equal("request-123", response.Headers.GetValues(GatewayHeaders.CorrelationId).Single());
        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task Authentication_RejectsInvalidTokens(string failure)
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        if (failure != "missing")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(failure));
        }

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
        Assert.Equal(0, host.DownstreamRequests);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(response.Headers.GetValues(GatewayHeaders.CorrelationId).Single(),
            problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Health_RequiresAllowedHostButBypassesAuthenticationAndExhaustedQuota()
    {
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["RateLimiting:Local:Service:PermitLimit"] = "1"
        });
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var second = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);

        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var invalidHost = new HttpRequestMessage(HttpMethod.Get, path);
            invalidHost.Headers.Host = "127.0.0.1";
            using var rejected = await host.Client.SendAsync(invalidHost);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
    }

    [Fact]
    public async Task ServiceConcurrency_RejectsBusyServiceWithoutBlockingAnotherService()
    {
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["RateLimiting:Local:ServiceConcurrencyLimit"] = "1",
            ["RateLimiting:Local:InstanceConcurrencyLimit"] = "2"
        });
        using var blockedRequest = host.AuthenticatedRequest(HttpMethod.Get, "/api/users/blocked");
        var blocked = host.Client.SendAsync(blockedRequest);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var rejected = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/users/42"));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            using var other = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        finally
        {
            host.Release.TrySetResult();
        }

        using var completed = await blocked;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var recovered = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task ServiceRateLimit_SharesAliasBudgetAndProtectsOtherServices()
    {
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["RateLimiting:Local:Service:PermitLimit"] = "1"
        });
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var rejected = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/users/42"));
        using var other = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Timeout_CancelsDownstreamAndReturnsGatewayTimeout(string method)
    {
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["Resilience:RequestTimeoutSeconds"] = "1",
            ["RateLimiting:Local:InstanceConcurrencyLimit"] = "1",
            ["RateLimiting:Local:ServiceConcurrencyLimit"] = "1"
        });
        using var response = await host.Client.SendAsync(host.AuthenticatedRequest(new HttpMethod(method), "/api/users/slow"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await host.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(response.Headers.Contains(GatewayHeaders.CorrelationId));
        Assert.Equal(1, host.DownstreamRequests);
        using var recovered = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", "198.51.100.7")]
    [InlineData("203.0.113.10", "203.0.113.10")]
    public async Task ForwardedHeaders_OnlyTrustConfiguredProxy(string peer, string expectedClient)
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var request = host.AuthenticatedRequest(HttpMethod.Get, "/api/users");
        request.Headers.Add("X-Test-Remote-IP", peer);
        request.Headers.Add("X-Forwarded-For", "198.51.100.7");
        request.Headers.Add("X-Forwarded-Host", "gateway.test");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = (await response.Content.ReadFromJsonAsync<ProxyEcho>())!;
        Assert.Equal(expectedClient, echo.ForwardedFor);
    }

    [Fact]
    public async Task Cors_AllowsConfiguredPreflightWithoutToken()
    {
        await using var host = await GatewayTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/users");
        request.Headers.Add("Origin", "https://client.test");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://client.test", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task ClientCancellation_ReleasesConcurrencyAndCancelsBackend()
    {
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["RateLimiting:Local:InstanceConcurrencyLimit"] = "1",
            ["RateLimiting:Local:ServiceConcurrencyLimit"] = "1"
        });
        using var cancellation = new CancellationTokenSource();
        using var request = host.AuthenticatedRequest(HttpMethod.Post, "/api/users/slow");
        var pending = host.Client.SendAsync(request, cancellation.Token);

        await WaitUntilAsync(() => host.DownstreamRequests == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await host.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, host.DownstreamRequests);
        using var recovered = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, cancellation.Token);
    }

    private sealed record ProxyEcho(string Path, string Query, string Method, string Body, string Authorization,
        string UserId, string TenantId, string MerchantId, string CorrelationId, string ForwardedFor,
        string AuthIssuer = "", string ApiKey = "", string Signature = "");

    private sealed class GatewayTestHost : IAsyncDisposable
    {
        private const string Issuer = "https://identity.test";
        private const string Audience = "gateway-api";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly WebApplication _downstream;
        private WebApplication _gateway = null!;
        private int _downstreamRequests;

        private GatewayTestHost(WebApplication downstream)
        {
            _downstream = downstream;
            BackendSigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        }

        public HttpClient Client { get; private set; } = null!;
        public IServiceProvider Services => _gateway.Services;
        public SecurityKey BackendSigningKey { get; set; }
        public int DownstreamRequests => _downstreamRequests;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<GatewayTestHost> StartAsync(Dictionary<string, string?>? overrides = null,
            Action<JwtBearerOptions, SecurityKey>? configureAuthentication = null,
            Action<WebApplicationBuilder>? configureGateway = null,
            string authenticationScheme = JwtBearerDefaults.AuthenticationScheme,
            Dictionary<string, TokenValidationParameters>? backendProviders = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var downstream = builder.Build();
            var host = new GatewayTestHost(downstream);
            downstream.MapMethods("/api/{service}/{**rest}", ["GET", "POST", "PUT", "PATCH", "DELETE"], async context =>
            {
                Interlocked.Increment(ref host._downstreamRequests);
                var token = context.Request.Headers.Authorization.ToString();
                var handler = new JwtSecurityTokenHandler();
                var rawToken = token.Length > "Bearer ".Length ? token["Bearer ".Length..] : null;
                if (rawToken is not null) handler.ValidateToken(rawToken, backendProviders is not null
                    ? backendProviders[handler.ReadJwtToken(rawToken).Issuer] : new TokenValidationParameters
                    {
                        ValidIssuer = Issuer,
                        ValidAudience = Audience,
                        IssuerSigningKey = host.BackendSigningKey,
                        ClockSkew = TimeSpan.Zero
                    }, out _);
                if (context.Request.Path.Value!.EndsWith("/blocked", StringComparison.Ordinal))
                {
                    host.Entered.TrySetResult();
                    await host.Release.Task.WaitAsync(context.RequestAborted);
                }

                if (context.Request.Path.Value.EndsWith("/slow", StringComparison.Ordinal))
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), context.RequestAborted);
                    }
                    catch (OperationCanceledException)
                    {
                        host.Cancelled.TrySetResult();
                        return;
                    }
                }

                using var reader = new StreamReader(context.Request.Body);
                context.Response.Headers["X-Powered-By"] = "test-downstream";
                await context.Response.WriteAsJsonAsync(new ProxyEcho(
                    context.Request.Path, context.Request.QueryString.Value ?? "", context.Request.Method,
                    await reader.ReadToEndAsync(context.RequestAborted), token,
                    context.Request.Headers[GatewayHeaders.UserId].ToString(),
                    context.Request.Headers[GatewayHeaders.TenantId].ToString(),
                    context.Request.Headers[GatewayHeaders.MerchantId].ToString(),
                    context.Request.Headers[GatewayHeaders.CorrelationId].ToString(),
                    context.Request.Headers["X-Forwarded-For"].ToString(),
                    context.Request.Headers[GatewayHeaders.AuthIssuer].ToString(),
                    context.Request.Headers["X-Api-Key"].ToString(),
                    context.Request.Headers["X-Signature"].ToString()));
            });
            try
            {
                await downstream.StartAsync();
                host._gateway = CreateGateway(downstream.Urls.Single(), new RsaSecurityKey(host._rsa) { KeyId = "test-key" },
                    overrides, configureAuthentication, configureGateway, authenticationScheme);
                await host._gateway.StartAsync();
                host.Client = host._gateway.GetTestClient();
                host.Client.BaseAddress = new Uri("http://gateway.test");
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public HttpRequestMessage AuthenticatedRequest(HttpMethod method, string path, string userId = "user-a",
            string? tenantId = null, string? apiKeyId = null, IEnumerable<Claim>? claims = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                CreateToken(userId: userId, tenantId: tenantId, apiKeyId: apiKeyId, extraClaims: claims));
            return request;
        }

        public string CreateToken(string? failure = null, string userId = "user-a", string? tenantId = null, string? apiKeyId = null,
            IEnumerable<Claim>? extraClaims = null)
        {
            using var wrongKey = failure is "signature" or "rotation" ? RSA.Create(2048) : null;
            var claims = new List<Claim> { new("sub", userId) };
            claims.AddRange(extraClaims ?? []);
            if (tenantId is not null) claims.Add(new Claim("tenant_id", tenantId));
            if (apiKeyId is not null) claims.Add(new Claim("api_key_id", apiKeyId));
            var token = new JwtSecurityToken(
                issuer: failure == "issuer" ? "https://wrong.test" : Issuer,
                audience: failure == "audience" ? "wrong-api" : Audience,
                claims: claims,
                notBefore: DateTime.UtcNow.AddMinutes(-10),
                expires: DateTime.UtcNow.AddMinutes(failure == "expired" ? -2 : 10),
                signingCredentials: new SigningCredentials(new RsaSecurityKey(wrongKey ?? _rsa)
                {
                    KeyId = failure == "rotation" ? "new-key" : "test-key"
                }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            Client?.Dispose();
            if (_gateway is not null)
            {
                await _gateway.DisposeAsync();
            }

            await _downstream.DisposeAsync();
            _rsa.Dispose();
        }

        private static WebApplication CreateGateway(string destination, SecurityKey key, Dictionary<string, string?>? overrides,
            Action<JwtBearerOptions, SecurityKey>? configureAuthentication, Action<WebApplicationBuilder>? configureGateway,
            string authenticationScheme)
        {
            var values = new Dictionary<string, string?>
            {
                ["environment"] = "Production",
                ["contentRoot"] = AppContext.BaseDirectory,
                ["Authentication:Enabled"] = "true",
                ["Authentication:Jwt:Authority"] = Issuer,
                ["Authentication:Jwt:Audience"] = Audience,
                ["Authentication:Jwt:ClockSkewSeconds"] = "0",
                ["GatewaySecurity:AllowedHosts:0"] = "gateway.test",
                ["GatewaySecurity:KnownProxies:0"] = "127.0.0.1",
                ["GatewaySecurity:AllowedForwardedHosts:0"] = "gateway.test",
                ["Observability:OtlpExporterEnabled"] = "false",
                ["Cors:Enabled"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://client.test"
            };
            foreach (var name in new[] { "user", "order", "product" })
            {
                values[$"ReverseProxy:Clusters:{name}-cluster:Destinations:{name}-api:Address"] = destination;
            }

            foreach (var pair in overrides ?? [])
            {
                values[pair.Key] = pair.Value;
            }

            return Program.CreateApplication(values.Select(pair => $"--{pair.Key}={pair.Value}").ToArray(), builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Services.PostConfigure<JwtBearerOptions>(authenticationScheme, options =>
                {
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    options.Configuration.SigningKeys.Add(key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
                    configureAuthentication?.Invoke(options, key);
                });
                builder.Services.AddSingleton<IStartupFilter, TestRemoteIpFilter>();
                configureGateway?.Invoke(builder);
            });
        }

        private sealed class TestRemoteIpFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use((context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(
                        context.Request.Headers["X-Test-Remote-IP"].FirstOrDefault() ?? "203.0.113.10");
                    if (int.TryParse(context.Request.Headers["X-Test-Local-Port"].FirstOrDefault(), out var localPort))
                        context.Connection.LocalPort = localPort;
                    return continuation(context);
                });
                next(app);
            };
        }
    }
}
