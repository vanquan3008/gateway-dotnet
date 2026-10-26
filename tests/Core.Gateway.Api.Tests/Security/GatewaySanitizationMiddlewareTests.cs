using System.Text.Json;
using Core.Gateway.Api.Security.Waf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Core.Gateway.Api.Tests.Security;

public sealed class GatewaySanitizationMiddlewareTests
{
    private readonly NullLogger<GatewaySanitizationMiddleware> _logger = NullLogger<GatewaySanitizationMiddleware>.Instance;

    [Theory]
    [InlineData("/api/users", "?page=1&limit=10")]
    [InlineData("/api/orders/123", "?status=completed")]
    [InlineData("/api/products/search", "?q=laptop+pro")]
    public async Task LegitimateRequest_PassesThrough(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        var called = false;
        var middleware = new GatewaySanitizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, _logger);

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/users\0admin", "")]
    [InlineData("/api/users", "?name=admin%00")]
    public async Task NullByte_IsBlocked(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new GatewaySanitizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, _logger);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body);
        Assert.NotNull(problem);
        Assert.Equal("SECURITY_POLICY_VIOLATION", problem.Extensions["code"]?.ToString());
    }

    [Theory]
    [InlineData("/api/../etc/passwd", "")]
    [InlineData("/api/..\\windows\\win.ini", "")]
    [InlineData("/api/files", "?file=%2e%2e/secret")]
    public async Task PathTraversal_IsBlocked(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new GatewaySanitizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, _logger);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/users", "?redirect=javascript:alert(1)")]
    [InlineData("/api/search", "?q=<script>alert('xss')</script>")]
    public async Task ScriptInjection_IsBlocked(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new GatewaySanitizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, _logger);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/users", "?id=1' OR 1=1")]
    [InlineData("/api/items", "?sort=name; DROP TABLE users")]
    [InlineData("/api/products", "?cat=1 UNION ALL SELECT null,null")]
    public async Task SqlInjection_IsBlocked(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new GatewaySanitizationMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, _logger);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }
}
