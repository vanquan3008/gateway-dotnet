using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.RateLimiting;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Core.Gateway.Api.Tests.Configuration;

public sealed class GatewayRouteMetadataConfigFilterTests
{
    private readonly GatewayRouteMetadataConfigFilter _filter = new(Options.Create(new GatewayRateLimitingOptions()));

    [Fact]
    public async Task StreamingRoute_HasNoRequestTimeout()
    {
        var route = await _filter.ConfigureRouteAsync(Route(streaming: "true"), null, CancellationToken.None);

        Assert.Equal("Disable", route.TimeoutPolicy);
    }

    [Fact]
    public async Task StreamingRoute_MayAlreadyDisableTheTimeout()
    {
        var route = await _filter.ConfigureRouteAsync(Route(streaming: "true", timeoutPolicy: "disable"), null, CancellationToken.None);

        Assert.Equal("Disable", route.TimeoutPolicy);
    }

    [Fact]
    public async Task StreamingRoute_CannotAlsoNameATimeoutPolicy()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _filter.ConfigureRouteAsync(Route(streaming: "true", timeoutPolicy: "api"), null, CancellationToken.None));

        Assert.Contains("TimeoutPolicy 'api'", exception.Message);
    }

    [Fact]
    public async Task NonStreamingRoute_KeepsItsTimeoutPolicy()
    {
        var route = await _filter.ConfigureRouteAsync(Route(streaming: "false", timeoutPolicy: "api"), null, CancellationToken.None);

        Assert.Equal("api", route.TimeoutPolicy);
    }

    [Fact]
    public async Task StreamingFlag_MustBeABoolean()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _filter.ConfigureRouteAsync(Route(streaming: "yes"), null, CancellationToken.None));
    }

    private static RouteConfig Route(string streaming, string? timeoutPolicy = null) => new()
    {
        RouteId = "events",
        ClusterId = "events-cluster",
        TimeoutPolicy = timeoutPolicy,
        Match = new RouteMatch { Path = "/api/events/{**rest}" },
        Metadata = new Dictionary<string, string> { [GatewayRouteMetadata.Streaming] = streaming }
    };
}
