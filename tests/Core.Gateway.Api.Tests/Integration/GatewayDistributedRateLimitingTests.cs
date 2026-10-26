using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.Tests.Infrastructure;
using Core.Gateway.Api.Transformations;
using StackExchange.Redis;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    private static Dictionary<string, string?> DistributedSettings(string? connection = null, bool failOpen = true) => new()
    {
        ["RateLimiting:Distributed:Enabled"] = "true",
        ["RateLimiting:Distributed:RedisConnectionString"] = connection ?? Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS")!,
        ["RateLimiting:Distributed:KeyPrefix"] = $"test:gateway:{Guid.NewGuid():N}",
        ["RateLimiting:Distributed:FailOpen"] = failOpen.ToString(),
        ["RateLimiting:Distributed:TimeoutMilliseconds"] = "500",
        ["RateLimiting:Distributed:ConnectTimeoutMilliseconds"] = "200",
        ["RateLimiting:Distributed:FailureCooldownSeconds"] = "1"
    };

    [Fact]
    public async Task LocalRateRejectedRequest_DoesNotCallDistributedStore()
    {
        var settings = DistributedSettings("");
        settings["RateLimiting:Local:Instance:PermitLimit"] = "1";
        var acquisitions = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == DistributedRateLimitingMiddleware.MeterName &&
                instrument.Name == "gateway.ratelimit.redis.duration")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => acquisitions++);
        listener.Start();
        await using var host = await GatewayTestHost.StartAsync(settings);
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var rejected = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        await AssertProblemAsync(rejected, 429, requireRetryAfter: true);
        Assert.Equal(1, acquisitions);
        Assert.Equal(1, host.DownstreamRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedisUnavailable_LocalProtectionStillEnforcesEachPodBudgetBeforeFailurePolicy(bool failOpen)
    {
        using var reserved = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var settings = DistributedSettings($"127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}", failOpen);
        settings["RateLimiting:Local:Instance:PermitLimit"] = "2";
        settings["RateLimiting:Distributed:Authenticated:PermitLimit"] = "1";
        await using var first = await GatewayTestHost.StartAsync(settings);
        await using var second = await GatewayTestHost.StartAsync(settings);
        var expected = failOpen ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
        for (var i = 0; i < 2; i++)
        {
            using var admitted = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
            Assert.Equal(expected, admitted.StatusCode);
        }

        using var localRejected = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        Assert.Equal(HttpStatusCode.TooManyRequests, localRejected.StatusCode);
        await AssertProblemAsync(localRejected, 429, requireRetryAfter: true);
        using var otherPod = await second.Client.SendAsync(second.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(expected, otherPod.StatusCode);
        using var live = await first.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(failOpen ? 2 : 0, first.DownstreamRequests);
        Assert.Equal(failOpen ? 1 : 0, second.DownstreamRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedisUnavailable_EnforcesFailurePolicyAndHealth(bool failOpen)
    {
        // Port is held without listening, so it cannot be taken by another process during the test.
        using var reserved = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var settings = DistributedSettings($"127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}", failOpen);
        settings["RateLimiting:Distributed:Authenticated:PermitLimit"] = "1";
        await using var host = await GatewayTestHost.StartAsync(settings);
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var second = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(failOpen ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(failOpen ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, second.StatusCode);
        if (!failOpen) await AssertProblemAsync(second, 503, requireRetryAfter: false);
        using var live = await host.Client.GetAsync("/health/live");
        using var ready = await host.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(failOpen ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var health = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(failOpen ? "Degraded" : "Unhealthy", health.GetProperty("status").GetString());
        Assert.Equal(failOpen ? 2 : 0, host.DownstreamRequests);
    }

    [Theory]
    [InlineData(false, "127.0.0.1:1", true)]
    [InlineData(false, "", false)]
    [InlineData(true, "", true)]
    [InlineData(true, "", false)]
    public async Task RedisDisabledOrUnconfigured_NeverCreatesLocalUserQuota(bool enabled, string connection, bool failOpen)
    {
        var settings = DistributedSettings(connection, failOpen);
        settings["RateLimiting:Distributed:Enabled"] = enabled.ToString();
        settings["RateLimiting:Distributed:Authenticated:PermitLimit"] = "1";
        await using var host = await GatewayTestHost.StartAsync(settings);
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var second = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        var expected = enabled && !failOpen ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
        Assert.Equal(expected, first.StatusCode);
        Assert.Equal(expected, second.StatusCode);
        if (expected == HttpStatusCode.ServiceUnavailable)
            await AssertProblemAsync(second, 503, requireRetryAfter: false);
        using var ready = await host.Client.GetAsync("/health/ready");
        var health = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(!enabled ? "Healthy" : failOpen ? "Degraded" : "Unhealthy", health.GetProperty("status").GetString());
    }

    [RedisFact]
    public async Task DistributedUserQuota_IsSharedAcrossGatewaysAndAliases()
    {
        var settings = DistributedSettings();
        settings["RateLimiting:Distributed:Authenticated:PermitLimit"] = "2";
        await using var first = await GatewayTestHost.StartAsync(settings);
        await using var second = await GatewayTestHost.StartAsync(settings);
        using var allowed1 = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var allowed2 = await second.Client.SendAsync(second.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        using var denied = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/users/42"));
        using var otherUser = await second.Client.SendAsync(second.AuthenticatedRequest(HttpMethod.Get, "/api/users", "user-b"));
        Assert.Equal(HttpStatusCode.OK, allowed1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
        await AssertProblemAsync(denied, 429, requireRetryAfter: true);
        using var ready = await second.Client.GetAsync("/health/ready");
        Assert.Equal("Healthy", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [RedisFact]
    public async Task LocalDisabled_DoesNotDisableSharedQuotaOrApplyLocalInstanceLimit()
    {
        var settings = DistributedSettings();
        settings["RateLimiting:Local:Enabled"] = "false";
        settings["RateLimiting:Local:Instance:PermitLimit"] = "1";
        settings["RateLimiting:Distributed:Authenticated:PermitLimit"] = "2";
        await using var host = await GatewayTestHost.StartAsync(settings);
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var second = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var denied = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        await AssertProblemAsync(denied, 429, requireRetryAfter: true);
    }

    [RedisFact]
    public async Task DistributedIpQuota_IsSharedAndUsesTrustedForwardedAddress()
    {
        var settings = DistributedSettings();
        settings["RateLimiting:Distributed:Anonymous:PermitLimit"] = "1";
        await using var first = await GatewayTestHost.StartAsync(settings);
        await using var second = await GatewayTestHost.StartAsync(settings);
        using var allowed = await first.Client.GetAsync("/api/users");
        // Allowed by quota, then rejected by authorization; that request still spends the quota.
        Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        using var rejected = await second.Client.GetAsync("/api/orders");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        await AssertProblemAsync(rejected, 429, requireRetryAfter: true);
        using var forwarded = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        forwarded.Headers.Add("X-Test-Remote-IP", "127.0.0.1");
        forwarded.Headers.Add("X-Forwarded-For", "198.51.100.8");
        using var otherIp = await second.Client.SendAsync(forwarded);
        Assert.Equal(HttpStatusCode.Unauthorized, otherIp.StatusCode);
        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        forged.Headers.Add("X-Forwarded-For", "198.51.100.9");
        using var sameIp = await second.Client.SendAsync(forged);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameIp.StatusCode);
    }

    [RedisFact]
    public async Task DistributedTenantQuota_UsesVerifiedClaimsAcrossUsersAndGateways()
    {
        var settings = DistributedSettings();
        settings["RateLimiting:Distributed:Tenant:PermitLimit"] = "1";
        await using var first = await GatewayTestHost.StartAsync(settings);
        await using var second = await GatewayTestHost.StartAsync(settings);
        using var allowed = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/users", "user-a", "tenant-a"));
        using var request = second.AuthenticatedRequest(HttpMethod.Get, "/api/orders", "user-b", "tenant-a");
        request.Headers.Add(GatewayHeaders.TenantId, "forged-tenant");
        using var denied = await second.Client.SendAsync(request);
        using var otherTenant = await second.Client.SendAsync(second.AuthenticatedRequest(HttpMethod.Get, "/api/users", "user-c", "tenant-b"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
    }

    [RedisFact]
    public async Task DistributedApiKeyQuota_UsesVerifiedIdClaimAcrossUsersAndGateways()
    {
        var settings = DistributedSettings();
        settings["RateLimiting:Distributed:ApiKey:PermitLimit"] = "1";
        await using var first = await GatewayTestHost.StartAsync(settings);
        await using var second = await GatewayTestHost.StartAsync(settings);
        using var allowed = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/users", apiKeyId: "key-a"));
        using var request = second.AuthenticatedRequest(HttpMethod.Get, "/api/orders", "user-b", apiKeyId: "key-a");
        request.Headers.Add("X-Api-Key", "forged-key");
        using var denied = await second.Client.SendAsync(request);
        using var otherKey = await second.Client.SendAsync(second.AuthenticatedRequest(HttpMethod.Get, "/api/users", "user-c", apiKeyId: "key-b"));
        using var headerOnly = second.AuthenticatedRequest(HttpMethod.Get, "/api/users", "user-d");
        headerOnly.Headers.Add("X-Api-Key", "key-a");
        using var noClaim = await second.Client.SendAsync(headerOnly);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        await AssertProblemAsync(denied, 429, requireRetryAfter: true);
        Assert.Equal(HttpStatusCode.OK, otherKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, noClaim.StatusCode);
    }

    [RedisFact]
    public async Task DistributedMode_KeepsServiceAndInstanceConcurrencyLocalAndReleasesLeases()
    {
        var settings = DistributedSettings();
        settings["RateLimiting:Local:ServiceConcurrencyLimit"] = "1";
        settings["RateLimiting:Local:InstanceConcurrencyLimit"] = "1";
        await using var first = await GatewayTestHost.StartAsync(settings);
        await using var second = await GatewayTestHost.StartAsync(settings);
        using var request = first.AuthenticatedRequest(HttpMethod.Get, "/api/users/blocked");
        var blocked = first.Client.SendAsync(request);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var service = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/users/42"));
            using var instance = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
            using var otherPod = await second.Client.SendAsync(second.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
            Assert.Equal(HttpStatusCode.TooManyRequests, service.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, instance.StatusCode);
            Assert.Null(instance.Headers.RetryAfter);
            Assert.Equal(HttpStatusCode.OK, otherPod.StatusCode);
            await using var redis = await ConnectionMultiplexer.ConnectAsync(settings["RateLimiting:Distributed:RedisConnectionString"]!);
            var counter = redis.GetServer(redis.GetEndPoints().First())
                .Keys(pattern: settings["RateLimiting:Distributed:KeyPrefix"] + ":user:*").Single();
            // Busy requests must be rejected before they spend shared quota or call Redis.
            Assert.Equal("2", (await redis.GetDatabase().StringGetAsync(counter)).ToString());
        }
        finally { first.Release.TrySetResult(); }
        using var completed = await blocked;
        using var recovered = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [RedisFact]
    public async Task DistributedMode_KeepsEmergencyAndServiceRateBudgetsLocal()
    {
        var settings = DistributedSettings();
        settings["RateLimiting:Local:Service:PermitLimit"] = "1";
        settings["RateLimiting:Local:Instance:PermitLimit"] = "2";
        await using var first = await GatewayTestHost.StartAsync(settings);
        await using var second = await GatewayTestHost.StartAsync(settings);
        using var user = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        using var alias = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/users/42"));
        using var order = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        using var instance = await first.Client.SendAsync(first.AuthenticatedRequest(HttpMethod.Get, "/api/products"));
        using var otherPod = await second.Client.SendAsync(second.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, user.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, alias.StatusCode);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, instance.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherPod.StatusCode);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(settings["RateLimiting:Distributed:RedisConnectionString"]!);
        var counter = redis.GetServer(redis.GetEndPoints().First())
            .Keys(pattern: settings["RateLimiting:Distributed:KeyPrefix"] + ":user:*").Single();
        // Local rate rejection must not call Redis or spend the global user quota.
        Assert.Equal("3", (await redis.GetDatabase().StringGetAsync(counter)).ToString());
    }

    [RedisFact]
    public async Task RedisFailureDuringTraffic_BypassesSharedQuotaOrReturns503ThenRestoresCounter()
    {
        await using var admin = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS")!);
        var server = admin.GetServer(admin.GetEndPoints().First());
        var name = $"gateway-test-{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        await server.ExecuteAsync("ACL", "SETUSER", name, "on", ">" + password, "~*", "&*", "+@all");
        try
        {
            var configuration = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS")!);
            configuration.User = name;
            configuration.Password = password;
            foreach (var failOpen in new[] { true, false })
            {
                var settings = DistributedSettings(configuration.ToString(includePassword: true), failOpen);
                settings["RateLimiting:Distributed:Authenticated:PermitLimit"] = "2";
                await using var host = await GatewayTestHost.StartAsync(settings);
                using var before = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
                Assert.Equal(HttpStatusCode.OK, before.StatusCode);
                await server.ExecuteAsync("ACL", "SETUSER", name, "-eval", "-evalsha");
                using var during = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
                Assert.Equal(failOpen ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, during.StatusCode);
                // Outage traffic exceeds the distributed limit of two; no local user counter may appear.
                for (var i = 0; i < 3; i++)
                {
                    using var more = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
                    Assert.Equal(failOpen ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, more.StatusCode);
                }
                using var live = await host.Client.GetAsync("/health/live");
                using var ready = await host.Client.GetAsync("/health/ready");
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                Assert.Equal(failOpen ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, ready.StatusCode);
                await server.ExecuteAsync("ACL", "SETUSER", name, "+eval", "+evalsha");
                await Task.Delay(1_100);
                using var after = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
                using var denied = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
                Assert.Equal(HttpStatusCode.OK, after.StatusCode);
                Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
            }
        }
        finally { await server.ExecuteAsync("ACL", "DELUSER", name); }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, bool requireRetryAfter)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, problem.GetProperty("status").GetInt32());
        Assert.Equal(response.Headers.GetValues(GatewayHeaders.CorrelationId).Single(),
            problem.GetProperty("correlationId").GetString());
        if (requireRetryAfter) Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }
}
