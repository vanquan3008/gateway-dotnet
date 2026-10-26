using Xunit;

namespace Core.Gateway.Api.Tests.Infrastructure;

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GATEWAY_TEST_REDIS")))
            Skip = "Set GATEWAY_TEST_REDIS to run against a real isolated Redis (tests/test-redis.ps1).";
    }
}

[CollectionDefinition("Redis rate limiting", DisableParallelization = true)]
public sealed class RedisRateLimitingCollection;
