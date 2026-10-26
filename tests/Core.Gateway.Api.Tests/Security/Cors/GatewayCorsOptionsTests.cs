using Core.Gateway.Api.Security.Cors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.Security.Cors;

public sealed class GatewayCorsOptionsTests
{
    [Fact]
    public void AddGatewayCors_RejectsOriginWithTrailingSlash()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:Enabled"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://app.example.com/"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddGatewayCors(configuration)
            .BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GatewayCorsOptions>>().Value);

        Assert.Contains("invalid origin", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
