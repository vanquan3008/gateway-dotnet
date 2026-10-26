using Core.Gateway.Api.Authentication.Jwt;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Core.Gateway.Api.Tests.Authentication.Jwt;

public sealed class GatewayBearerTokenTests
{
    [Theory]
    [InlineData("Bearer abc.def.ghi", true, "abc.def.ghi")]
    [InlineData("bearer abc", true, "abc")]
    [InlineData("Bearer    abc  ", true, "abc")]
    [InlineData("Basic dXNlcjpwYXNz", false, "")]
    [InlineData("abc.def.ghi", false, "")]
    [InlineData("Bearer ", false, "")]
    [InlineData("", false, "")]
    public void TryGet_OnlyAcceptsABearerCredential(string authorization, bool found, string expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = authorization;

        Assert.Equal(found, GatewayBearerToken.TryGet(request, out var token));
        Assert.Equal(expected, token);
    }
}
