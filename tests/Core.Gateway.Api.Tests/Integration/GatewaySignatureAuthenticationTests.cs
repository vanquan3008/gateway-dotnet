using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    private const string PartnerClient = "partner-a";

    private static Dictionary<string, string?> SignatureSettings(string publicKeyFile, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Authentication:Signature:Enabled"] = "true",
            ["Redis:ConnectionString"] = "127.0.0.1:1",
            ["Redis:ConnectTimeoutMilliseconds"] = "100",
            ["Redis:TimeoutMilliseconds"] = "50",
            [$"Authentication:Signature:Clients:{PartnerClient}:PublicKeyFile"] = publicKeyFile,
            [$"Authentication:Signature:Clients:{PartnerClient}:Scopes:0"] = "orders.write",
            [$"Authentication:Signature:Clients:{PartnerClient}:TenantId"] = "tenant-7"
        };
        foreach (var pair in extra ?? [])
            settings[pair.Key] = pair.Value;
        return settings;
    }

    [Fact]
    public async Task Signature_AuthenticatesClientAndNeverForwardsTheSignatureHeaders()
    {
        using var partner = new PartnerKey();
        await using var host = await GatewayTestHost.StartAsync(SignatureSettings(partner.PublicKeyFile));

        using var response = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users?page=2"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = (await response.Content.ReadFromJsonAsync<ProxyEcho>())!;
        Assert.Equal(PartnerClient, echo.UserId);
        Assert.Equal("tenant-7", echo.TenantId);
        Assert.Equal("urn:core-gateway:signature", echo.AuthIssuer);
        Assert.Equal("?page=2", echo.Query);
        Assert.Equal("", echo.Signature);
        Assert.Equal("", echo.Authorization);
    }

    [Fact]
    public async Task Signature_CoversTheBodyAndStillForwardsItIntact()
    {
        using var partner = new PartnerKey();
        await using var host = await GatewayTestHost.StartAsync(SignatureSettings(partner.PublicKeyFile));

        using var genuine = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/orders", method: HttpMethod.Post, body: "{\"amount\":10}"));
        using var tampered = await host.Client.SendAsync(
            SignedRequest(partner.Rsa, "/api/orders", method: HttpMethod.Post, body: "{\"amount\":9999}", signedBody: "{\"amount\":10}"));

        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
        Assert.Equal("{\"amount\":10}", (await genuine.Content.ReadFromJsonAsync<ProxyEcho>())!.Body);
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        Assert.Equal(1, host.DownstreamRequests);
    }

    [Fact]
    public async Task Signature_RefusesABodyLargerThanTheLimit()
    {
        using var partner = new PartnerKey();
        await using var host = await GatewayTestHost.StartAsync(
            SignatureSettings(partner.PublicKeyFile, new() { ["Authentication:Signature:MaxBodyBytes"] = "16" }));

        using var small = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/orders", method: HttpMethod.Post, body: "tiny"));
        using var large = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/orders", method: HttpMethod.Post, body: new string('x', 64)));

        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, large.StatusCode);
    }

    [Fact]
    public async Task Signature_IsAcceptedOnlyOnce()
    {
        using var partner = new PartnerKey();
        await using var host = await GatewayTestHost.StartAsync(SignatureSettings(partner.PublicKeyFile));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var first = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", timestamp: timestamp));
        using var replay = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", timestamp: timestamp));
        using var next = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", timestamp: timestamp + 1));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Signature_ReplayProtectionCanBeSwitchedOff()
    {
        using var partner = new PartnerKey();
        await using var host = await GatewayTestHost.StartAsync(
            SignatureSettings(partner.PublicKeyFile, new() { ["Authentication:Signature:RejectReplays"] = "false" }));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var first = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", timestamp: timestamp));
        using var second = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", timestamp: timestamp));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Signature_AcceptsPssWhenConfigured()
    {
        using var partner = new PartnerKey();
        await using var host = await GatewayTestHost.StartAsync(
            SignatureSettings(partner.PublicKeyFile, new() { ["Authentication:Signature:Algorithm"] = "PS256" }));

        using var accepted = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", padding: RSASignaturePadding.Pss));
        using var rejected = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", padding: RSASignaturePadding.Pkcs1));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Theory]
    [InlineData("wrong-key", "invalid_signature")]
    [InlineData("other-path", "invalid_signature")]
    [InlineData("other-method", "invalid_signature")]
    [InlineData("stale", "expired")]
    [InlineData("future", "expired")]
    [InlineData("unknown-client", "unknown_client")]
    [InlineData("not-base64", "invalid_signature")]
    [InlineData("missing-timestamp", "malformed")]
    public async Task Signature_RejectsRequestsThatDoNotVerify_AndSaysWhyInTheMetric(string problem, string reason)
    {
        using var partner = new PartnerKey();
        using var stranger = RSA.Create(2048);
        await using var host = await GatewayTestHost.StartAsync(SignatureSettings(partner.PublicKeyFile));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var failures = new AuthenticationFailureListener();

        using var request = problem switch
        {
            "wrong-key" => SignedRequest(stranger, "/api/users"),
            "other-path" => SignedRequest(partner.Rsa, "/api/users", signedPath: "/api/orders"),
            "other-method" => SignedRequest(partner.Rsa, "/api/users", signedMethod: HttpMethod.Post),
            "stale" => SignedRequest(partner.Rsa, "/api/users", timestamp: now - 3_600),
            "future" => SignedRequest(partner.Rsa, "/api/users", timestamp: now + 3_600),
            "unknown-client" => SignedRequest(partner.Rsa, "/api/users", clientId: "nobody"),
            "not-base64" => WithHeader(SignedRequest(partner.Rsa, "/api/users"), "X-Signature", "###"),
            _ => WithHeader(SignedRequest(partner.Rsa, "/api/users"), "X-Timestamp", null)
        };

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.DownstreamRequests);
        Assert.Contains(("signature", reason), failures.Seen);
    }

    [Fact]
    public async Task Signature_IsACredentialForTheLocalAnonymousLimiter()
    {
        using var partner = new PartnerKey();
        await using var host = await GatewayTestHost.StartAsync(SignatureSettings(partner.PublicKeyFile, new()
        {
            ["RateLimiting:Local:Anonymous:PermitLimit"] = "1",
            ["RateLimiting:Local:Anonymous:WindowSeconds"] = "60"
        }));

        for (var request = 0; request < 3; request++)
        {
            using var signed = await host.Client.SendAsync(SignedRequest(partner.Rsa, "/api/users", timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + request));
            Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        }

        using var anonymous = await host.Client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var limited = await host.Client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static HttpRequestMessage SignedRequest(
        RSA key,
        string path,
        string clientId = PartnerClient,
        long? timestamp = null,
        string? signedPath = null,
        HttpMethod? method = null,
        HttpMethod? signedMethod = null,
        string? body = null,
        string? signedBody = null,
        RSASignaturePadding? padding = null)
    {
        method ??= HttpMethod.Get;
        var stamp = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signedBody ?? body ?? "")));
        var signedText = $"{(signedMethod ?? method).Method}\n{signedPath ?? path}\n{stamp}\n{clientId}\n{bodyHash}";
        var signature = key.SignData(Encoding.UTF8.GetBytes(signedText), HashAlgorithmName.SHA256, padding ?? RSASignaturePadding.Pkcs1);

        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8);
        request.Headers.Add("X-Client-Id", clientId);
        request.Headers.Add("X-Timestamp", stamp);
        request.Headers.Add("X-Signature", Convert.ToBase64String(signature));
        return request;
    }

    private static HttpRequestMessage WithHeader(HttpRequestMessage request, string name, string? value)
    {
        request.Headers.Remove(name);
        if (value is not null)
            request.Headers.Add(name, value);
        return request;
    }

    private sealed class AuthenticationFailureListener : IDisposable
    {
        private readonly MeterListener _listener = new();

        public AuthenticationFailureListener()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Core.Gateway.Authentication")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? scheme = null, result = null, reason = null;
                foreach (var tag in tags)
                {
                    switch (tag.Key)
                    {
                        case "scheme": scheme = tag.Value as string; break;
                        case "result": result = tag.Value as string; break;
                        case "reason": reason = tag.Value as string; break;
                    }
                }

                if (result == "failure" && scheme is not null && reason is not null)
                    lock (Seen) Seen.Add((scheme, reason));
            });
            _listener.Start();
        }

        public List<(string Scheme, string Reason)> Seen { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private sealed class PartnerKey : IDisposable
    {
        public PartnerKey()
        {
            Rsa = RSA.Create(2048);
            PublicKeyFile = Path.Combine(Path.GetTempPath(), $"gateway-partner-{Guid.NewGuid():N}.pem");
            File.WriteAllText(PublicKeyFile, Rsa.ExportSubjectPublicKeyInfoPem());
        }

        public RSA Rsa { get; }

        public string PublicKeyFile { get; }

        public void Dispose()
        {
            Rsa.Dispose();
            File.Delete(PublicKeyFile);
        }
    }
}
