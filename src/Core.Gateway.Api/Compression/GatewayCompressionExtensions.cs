using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace Core.Gateway.Api.Compression;

public static class GatewayCompressionExtensions
{
    public const string EnableForHttpsKey = "Compression:EnableForHttps";

    /// <summary>
    /// Adds Brotli and Gzip at the fastest level; HTTPS only when Compression:EnableForHttps is true.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayResponseCompression(this IServiceCollection services, IConfiguration configuration)
    {
        var enableForHttps = configuration.GetValue<bool>(EnableForHttpsKey);
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = enableForHttps;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
            [
                "application/json",
                "application/problem+json",
                "application/xml",
                "text/plain"
            ]);
        });

        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        return services;
    }
}
