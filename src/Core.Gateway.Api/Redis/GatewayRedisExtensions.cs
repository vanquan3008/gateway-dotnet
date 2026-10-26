using StackExchange.Redis;

namespace Core.Gateway.Api.Redis;

public static class GatewayRedisExtensions
{
    /// <summary>
    /// Registers the Redis options and the shared connection, once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayRedis(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IRedisConnection)))
        {
            return services;
        }
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options =>
            {
                if (!options.IsConfigured)
                {
                    return true;
                }
                try
                {
                    return ConfigurationOptions.Parse(options.ConnectionString!).EndPoints.Count > 0;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }, "Redis:ConnectionString is not a valid Redis connection string.")
            .ValidateOnStart();

        services.AddSingleton<IRedisConnection, RedisConnection>();
        return services;
    }
}
