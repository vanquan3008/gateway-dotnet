using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    [Fact]
    public async Task BackendFailure_UsesHealthyDestinationForNewRequestsAndRecoversWithoutRetryingPost()
    {
        using var reserved = ReserveBackendPort();
        var port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["ReverseProxy:Clusters:user-cluster:LoadBalancingPolicy"] = "FirstAlphabetical",
            ["ReverseProxy:Clusters:user-cluster:Destinations:a-failed:Address"] = $"http://127.0.0.1:{port}/",
            ["Resilience:BackendHealthDetectionWindowSeconds"] = "2",
            ["Resilience:BackendHealthReactivationSeconds"] = "3"
        }, configureGateway: builder => builder.Services.PostConfigure<TransportFailureRateHealthPolicyOptions>(options =>
            options.MinimalTotalCountThreshold = 1));

        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Post, "/api/users"));
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        await AssertProblemAsync(first, 502, requireRetryAfter: false);
        Assert.Equal(0, host.DownstreamRequests);
        await WaitUntilAsync(() => DestinationIsUnhealthy(host, "a-failed"));
        using var next = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Post, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(1, host.DownstreamRequests);

        reserved.Dispose();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        await using var recoveredBackend = builder.Build();
        var recoveredRequests = 0;
        recoveredBackend.MapPost("/api/users", () =>
        {
            Interlocked.Increment(ref recoveredRequests);
            return Results.Ok();
        });
        await recoveredBackend.StartAsync();
        await WaitUntilAsync(() => !DestinationIsUnhealthy(host, "a-failed"));
        using var recovered = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Post, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(1, recoveredRequests);
        Assert.Equal(1, host.DownstreamRequests);
        Assert.True(host.Services.GetRequiredService<IProxyStateLookup>().TryGetCluster("user-cluster", out var cluster));
        await WaitUntilAsync(() => cluster.Destinations["a-failed"].Health.Passive == DestinationHealth.Healthy);
        using var stable = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Post, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, stable.StatusCode);
        Assert.Equal(2, recoveredRequests);
        Assert.Equal(1, host.DownstreamRequests);
    }

    [Fact]
    public async Task AllBackendDestinationsUnhealthy_Returns503WithoutTakingOtherServicesOrHealthDown()
    {
        using var firstPort = ReserveBackendPort();
        using var secondPort = ReserveBackendPort();
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["ReverseProxy:Clusters:user-cluster:LoadBalancingPolicy"] = "FirstAlphabetical",
            ["ReverseProxy:Clusters:user-cluster:Destinations:a-failed:Address"] = $"http://{firstPort.LocalEndPoint}/",
            ["ReverseProxy:Clusters:user-cluster:Destinations:user-api:Address"] = $"http://{secondPort.LocalEndPoint}/",
            ["Resilience:BackendHealthDetectionWindowSeconds"] = "30",
            ["Resilience:BackendHealthReactivationSeconds"] = "31",
            ["Resilience:FailWhenAllDestinationsUnhealthy"] = "true"
        }, configureGateway: builder => builder.Services.PostConfigure<TransportFailureRateHealthPolicyOptions>(options =>
            options.MinimalTotalCountThreshold = 1));

        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Post, "/api/users"));
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        await WaitUntilAsync(() => DestinationIsUnhealthy(host, "a-failed"));
        using var second = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Post, "/api/users"));
        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);
        await WaitUntilAsync(() => DestinationIsUnhealthy(host, "user-api"));
        using var unavailable = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Post, "/api/users"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        await AssertProblemAsync(unavailable, 503, requireRetryAfter: false);
        Assert.Equal(0, host.DownstreamRequests);

        using var order = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var health = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    [Fact]
    public async Task RouteWithoutTimeoutPolicy_UsesDefaultTimeoutAndRecordsProxyError()
    {
        var timedOut = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Core.Gateway.Proxy" && instrument.Name == "gateway.proxy.errors")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "error", Value: "RequestTimedOut" })
                    Interlocked.Add(ref timedOut, value);
            }
        });
        listener.Start();

        await using var host = await GatewayTestHost.StartAsync(new()
        {
            // user-cluster keeps its 20s ActivityTimeout, so only the default request policy can stop this.
            ["Resilience:RequestTimeoutSeconds"] = "1",
            ["ReverseProxy:Routes:untimed-route:ClusterId"] = "user-cluster",
            ["ReverseProxy:Routes:untimed-route:Match:Path"] = "/api/users/untimed/{**catch-all}"
        });

        using var response = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users/untimed/slow"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await host.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Interlocked.Read(ref timedOut) >= 1);
    }

    private static Socket ReserveBackendPort()
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static bool DestinationIsUnhealthy(GatewayTestHost host, string destination)
    {
        Assert.True(host.Services.GetRequiredService<IProxyStateLookup>().TryGetCluster("user-cluster", out var cluster));
        return cluster.Destinations[destination].Health.Passive == DestinationHealth.Unhealthy;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, cancellation.Token);
    }
}
