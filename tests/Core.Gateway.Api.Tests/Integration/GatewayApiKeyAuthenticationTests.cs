using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    private const string BillingKey = "billing-4f1c2d9e8b7a6c5d4e3f2a1b0c9d8e7f";
    private const string ReportingKey = "reporting-9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d";
    private const string RetiredKey = "retired-0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d";

    private static Dictionary<string, string?> ApiKeySettings(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:ApiKeys:Enabled"] = "true",
            ["Authentication:ApiKeys:Clients:billing:KeyHash"] = Sha256(BillingKey),
            ["Authentication:ApiKeys:Clients:billing:Scopes:0"] = "orders.write",
            ["Authentication:ApiKeys:Clients:billing:TenantId"] = "tenant-9",
            ["Authentication:ApiKeys:Clients:reporting:KeyHash"] = Sha256(ReportingKey),
            ["Authentication:ApiKeys:Clients:retired:KeyHash"] = Sha256(RetiredKey),
            ["Authentication:ApiKeys:Clients:retired:Enabled"] = "false"
        };
        foreach (var pair in extra ?? [])
            settings[pair.Key] = pair.Value;
        return settings;
    }

    [Fact]
    public async Task ApiKey_AuthenticatesClientAndNeverForwardsTheKey()
    {
        await using var host = await GatewayTestHost.StartAsync(ApiKeySettings());

        using var response = await host.Client.SendAsync(ApiKeyRequest("/api/users", BillingKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = (await response.Content.ReadFromJsonAsync<ProxyEcho>())!;
        Assert.Equal("billing", echo.UserId);
        Assert.Equal("tenant-9", echo.TenantId);
        Assert.Equal("urn:core-gateway:api-key", echo.AuthIssuer);
        Assert.Equal("", echo.ApiKey);
        Assert.Equal("", echo.Authorization);
    }

    [Theory]
    [InlineData(BillingKey, HttpStatusCode.OK)]
    [InlineData(ReportingKey, HttpStatusCode.OK)]
    [InlineData(RetiredKey, HttpStatusCode.Unauthorized)]
    [InlineData("unknown-key", HttpStatusCode.Unauthorized)]
    public async Task ApiKey_AcceptsOnlyRegisteredEnabledKeys(string key, HttpStatusCode expected)
    {
        await using var host = await GatewayTestHost.StartAsync(ApiKeySettings());

        using var response = await host.Client.SendAsync(ApiKeyRequest("/api/orders", key));

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, host.DownstreamRequests);
    }

    [Fact]
    public async Task ApiKey_DoesNotReplaceBearerTokens()
    {
        await using var host = await GatewayTestHost.StartAsync(ApiKeySettings());

        using var response = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = (await response.Content.ReadFromJsonAsync<ProxyEcho>())!;
        Assert.Equal("user-a", echo.UserId);
        Assert.Equal("https://identity.test", echo.AuthIssuer);
    }

    [Fact]
    public async Task ApiKey_IsACredentialForTheLocalAnonymousLimiter()
    {
        await using var host = await GatewayTestHost.StartAsync(ApiKeySettings(new()
        {
            ["RateLimiting:Local:Anonymous:PermitLimit"] = "1",
            ["RateLimiting:Local:Anonymous:WindowSeconds"] = "60"
        }));

        for (var request = 0; request < 3; request++)
        {
            using var keyed = await host.Client.SendAsync(ApiKeyRequest("/api/users", BillingKey));
            Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
        }

        using var anonymous = await host.Client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var limited = await host.Client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Theory]
    [InlineData("X-Api-Key", "not-a-registered-key")]
    [InlineData("Authorization", "Bearer not-a-token")]
    [InlineData("X-Signature", "AAAA")]
    public async Task JunkCredentialHeader_DoesNotEscapeTheAnonymousLimiter(string header, string value)
    {
        await using var host = await GatewayTestHost.StartAsync(ApiKeySettings(new()
        {
            ["RateLimiting:Local:Anonymous:PermitLimit"] = "1",
            ["RateLimiting:Local:Anonymous:WindowSeconds"] = "60"
        }));

        using var first = await host.Client.SendAsync(WithJunk(header, value));
        using var second = await host.Client.SendAsync(WithJunk(header, value));

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(0, host.DownstreamRequests);

        static HttpRequestMessage WithJunk(string header, string value)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");
            request.Headers.TryAddWithoutValidation(header, value);
            return request;
        }
    }

    private static HttpRequestMessage ApiKeyRequest(string path, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", key);
        return request;
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
