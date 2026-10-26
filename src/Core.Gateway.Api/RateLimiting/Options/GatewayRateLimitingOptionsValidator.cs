using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Core.Gateway.Api.RateLimiting.Options;

public sealed class GatewayRateLimitingOptionsValidator : IValidateOptions<GatewayRateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayRateLimitingOptions options)
    {
        var errors = new List<string>();

        ValidateNestedOptions(options.Local, nameof(options.Local), errors);
        ValidateNestedOptions(options.Distributed, nameof(options.Distributed), errors);

        if (options.Local is { } local)
        {
            ValidateNestedOptions(local.Instance, "Local:Instance", errors);
            ValidateNestedOptions(local.Service, "Local:Service", errors);
            if (local.Anonymous is not null)
                ValidateNestedOptions(local.Anonymous, "Local:Anonymous", errors);

            if (local.ServiceConcurrencyLimit > local.InstanceConcurrencyLimit)
                errors.Add("Local:ServiceConcurrencyLimit must not exceed Local:InstanceConcurrencyLimit.");
        }

        if (options.Distributed is { } distributed)
        {
            foreach (var (quotaName, limit) in new[] { ("Authenticated", distributed.Authenticated),
                ("Anonymous", distributed.Anonymous), ("Tenant", distributed.Tenant), ("ApiKey", distributed.ApiKey) })
            {
                if (limit is null && quotaName is "Tenant" or "ApiKey") continue;
                ValidateNestedOptions(limit, $"Distributed:{quotaName}", errors);
                if (limit is { QueueLimit: not 0 })
                    errors.Add($"Distributed:{quotaName}: QueueLimit must be zero; shared quotas do not queue requests.");
            }

            if (distributed.IsConfigured && !HasValidRedisConnectionString(distributed.RedisConnectionString!))
                errors.Add("Distributed: Redis connection configuration is invalid.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateNestedOptions(object? options, string propertyName, List<string> errors)
    {
        if (options is null)
        {
            errors.Add($"{propertyName} is required.");
            return;
        }

        var validationResults = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), validationResults, validateAllProperties: true);

        foreach (var result in validationResults)
        {
            errors.Add($"{propertyName}: {result.ErrorMessage}");
        }
    }

    private static bool HasValidRedisConnectionString(string connectionString)
    {
        try
        {
            return ConfigurationOptions.Parse(connectionString).EndPoints.Count > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
