using System.Net;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    [Fact]
    public async Task InjectionFilter_CanBeTurnedOffPerRouteWhileTraversalStaysBlocked()
    {
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["ReverseProxy:Routes:user-route:Metadata:InjectionFilter"] = "false"
        });

        using var optedOut = await host.Client.SendAsync(
            host.AuthenticatedRequest(HttpMethod.Get, "/api/users/search?q=JavaScript:%20The%20Good%20Parts"));
        using var filtered = await host.Client.SendAsync(
            host.AuthenticatedRequest(HttpMethod.Get, "/api/orders/search?q=JavaScript:%20The%20Good%20Parts"));
        using var traversal = await host.Client.SendAsync(
            host.AuthenticatedRequest(HttpMethod.Get, "/api/users/..%2Fadmin"));

        Assert.Equal(HttpStatusCode.OK, optedOut.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, filtered.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        Assert.Equal(1, host.DownstreamRequests);
    }
}
