using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Core.Gateway.Api.Tests.Infrastructure;

internal sealed class TestOidcBackchannel : HttpMessageHandler
{
    private string _keys = "";
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public bool Unavailable { get; set; }
    public bool BlockRequests { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Configure(JwtBearerOptions options, SecurityKey key)
    {
        SetSigningKey((RsaSecurityKey)key);
        options.Configuration = null;
        options.ConfigurationManager = null;
        options.BackchannelHttpHandler = this;
    }

    public void SetSigningKey(RsaSecurityKey key)
    {
        var parameters = key.Rsa.ExportParameters(false);
        _keys = JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid = key.KeyId, alg = "RS256", use = "sig",
                n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) } }
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult();
        if (BlockRequests) await Release.Task.WaitAsync(cancellationToken);
        if (Unavailable)
        {
            Failed.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        var json = request.RequestUri!.AbsolutePath == "/keys" ? _keys :
            JsonSerializer.Serialize(new { issuer = "https://identity.test", jwks_uri = "https://identity.test/keys" });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
