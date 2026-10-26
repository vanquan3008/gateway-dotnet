using Core.Gateway.Api.Security.Waf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Gateway.Api.Tests.Security;

public sealed class GatewaySanitizationEncodedQueryTests
{
    [Theory]
    [InlineData("?q=%3Cscript%3Ealert(1)%3C/script%3E")]
    [InlineData("?redirect=javascript%3Aalert(1)")]
    [InlineData("?id=1+UNION+SELECT+null")]
    [InlineData("?id=1%20union%20all%20select%20null")]
    [InlineData("?file=%252e%252e%252fsecret")]
    [InlineData("?file=..%5Csecret")]
    public async Task EncodedAttacks_AreBlocked(string query)
    {
        var (blocked, status) = await RunAsync(query);

        Assert.True(blocked);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Theory]
    [InlineData("?q=100%25+cotton")]
    [InlineData("?name=O%27Brien")]
    [InlineData("?city=S%C3%A3o+Paulo&page=2")]
    [InlineData("?q=100%")]
    public async Task EncodedButHarmlessQueries_PassThrough(string query)
    {
        var (blocked, status) = await RunAsync(query);

        Assert.False(blocked);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    private static async Task<(bool Blocked, int Status)> RunAsync(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/items";
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new GatewaySanitizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, NullLogger<GatewaySanitizationMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        return (!called, context.Response.StatusCode);
    }
}
