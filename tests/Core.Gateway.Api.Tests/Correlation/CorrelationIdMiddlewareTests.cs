using Core.Gateway.Api.Correlation;
using Core.Gateway.Api.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Gateway.Api.Tests.Correlation;

public sealed class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_PreservesValidCorrelationId()
    {
        const string expected = "request-123";
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayHeaders.CorrelationId] = expected;

        string? forwarded = null;
        var middleware = new CorrelationIdMiddleware(
            nextContext =>
            {
                forwarded = nextContext.Request.Headers[GatewayHeaders.CorrelationId];
                return Task.CompletedTask;
            },
            NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(expected, forwarded);
    }

    [Fact]
    public async Task InvokeAsync_ReplacesUntrustedCorrelationId()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayHeaders.CorrelationId] = "invalid value with spaces";

        string? forwarded = null;
        var middleware = new CorrelationIdMiddleware(
            nextContext =>
            {
                forwarded = nextContext.Request.Headers[GatewayHeaders.CorrelationId];
                return Task.CompletedTask;
            },
            NullLogger<CorrelationIdMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.NotNull(forwarded);
        Assert.Matches("^[a-f0-9]{32}$", forwarded);
    }
}
