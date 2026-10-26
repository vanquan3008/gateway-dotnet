using System.Net;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    private const string DeadDestination = "http://127.0.0.1:1/";

    [Fact]
    public async Task IdempotentRequest_RetriesOnAnotherDestinationWhenTheFirstIsUnreachable()
    {
        await using var host = await GatewayTestHost.StartAsync(TwoDestinations(retryAttempts: 1));

        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var request = host.AuthenticatedRequest(HttpMethod.Get, "/api/users/42");
            using var response = await host.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task WithoutRetries_AnUnreachableDestinationFailsTheRequest()
    {
        await using var host = await GatewayTestHost.StartAsync(TwoDestinations(retryAttempts: 0));

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var request = host.AuthenticatedRequest(HttpMethod.Get, "/api/users/42");
            using var response = await host.Client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.BadGateway, statuses);
        Assert.Contains(HttpStatusCode.OK, statuses);
    }

    [Fact]
    public async Task NonIdempotentRequest_IsNeverRetried()
    {
        await using var host = await GatewayTestHost.StartAsync(TwoDestinations(retryAttempts: 3));

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var request = host.AuthenticatedRequest(HttpMethod.Post, "/api/users");
            request.Content = new StringContent("{}");
            using var response = await host.Client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.BadGateway, statuses);
    }

    [Fact]
    public async Task SlowBackend_IsCutOffByTheRequestTimeoutWith504()
    {
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["Resilience:RequestTimeoutSeconds"] = "1",
            ["Resilience:ActivityTimeoutSeconds"] = "30",
            ["ReverseProxy:Clusters:user-cluster:HttpRequest:ActivityTimeout"] = "00:00:30"
        });

        using var request = host.AuthenticatedRequest(HttpMethod.Get, "/api/users/slow");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await host.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Dictionary<string, string?> TwoDestinations(int retryAttempts) => new()
    {
        ["Resilience:RetryAttempts"] = retryAttempts.ToString(),
        ["ReverseProxy:Clusters:user-cluster:LoadBalancingPolicy"] = "RoundRobin",
        ["ReverseProxy:Clusters:user-cluster:Destinations:dead:Address"] = DeadDestination
    };
}
