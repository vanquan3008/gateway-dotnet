using System.Text.RegularExpressions;
using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.RateLimiting.Routes;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.Extensions.Options;
using static Core.Gateway.Api.Configuration.OptionsErrorCollector;

namespace Core.Gateway.Api.RateLimiting;

public sealed partial class GatewayRateLimitingOptionsValidator : IValidateOptions<GatewayRateLimitingOptions>
{
    private const string Local = nameof(GatewayRateLimitingOptions.Local);
    private const string Distributed = nameof(GatewayRateLimitingOptions.Distributed);
    private const string Policies = nameof(GatewayRateLimitingOptions.Policies);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex PolicyName { get; }

    /// <summary>
    /// Validates the RateLimiting section at startup and reports all errors together.
    /// </summary>
    /// <param name="name">The options name (unused).</param>
    /// <param name="options">The options to check.</param>
    /// <returns>Success, or a failure listing every error.</returns>
    public ValidateOptionsResult Validate(string? name, GatewayRateLimitingOptions options)
    {
        var errors = new OptionsErrorCollector();

        ValidateLocal(options.Local, errors);
        ValidateDistributed(options.Distributed, errors);
        ValidatePolicies(options.Policies, errors);

        return errors.ToResult();
    }

    /// <summary>
    /// Checks the local limits and that the service concurrency limit does not exceed the instance limit.
    /// </summary>
    /// <param name="local">The local options.</param>
    /// <param name="errors">Collects errors.</param>
    private static void ValidateLocal(LocalRateLimitingOptions? local, OptionsErrorCollector errors)
    {
        if (!errors.Require(local, Local))
            return;

        errors.Require(local.Instance, Path(Local, nameof(local.Instance)));
        errors.Require(local.Service, Path(Local, nameof(local.Service)));
        errors.ValidateIfPresent(local.Anonymous, Path(Local, nameof(local.Anonymous)));

        if (local.ServiceConcurrencyLimit > local.InstanceConcurrencyLimit)
        {
            errors.Add($"{Path(Local, nameof(local.ServiceConcurrencyLimit))} must not exceed " +
                       $"{Path(Local, nameof(local.InstanceConcurrencyLimit))}.");
        }
    }

    /// <summary>
    /// Checks the shared quotas and the fallback section.
    /// </summary>
    /// <param name="distributed">The distributed options.</param>
    /// <param name="errors">Collects errors.</param>
    private static void ValidateDistributed(DistributedRateLimitingOptions? distributed, OptionsErrorCollector errors)
    {
        if (!errors.Require(distributed, Distributed))
            return;

        errors.Require(distributed.Fallback, Path(Distributed, nameof(distributed.Fallback)));

        ValidateSharedQuota(nameof(distributed.Authenticated), distributed.Authenticated, required: true, errors);
        ValidateSharedQuota(nameof(distributed.Anonymous), distributed.Anonymous, required: true, errors);
        ValidateSharedQuota(nameof(distributed.Tenant), distributed.Tenant, required: false, errors);
        ValidateSharedQuota(nameof(distributed.ApiKey), distributed.ApiKey, required: false, errors);
    }

    /// <summary>
    /// Checks one shared quota: required when asked, and no queue.
    /// </summary>
    /// <param name="quotaName">The quota name.</param>
    /// <param name="limit">The quota.</param>
    /// <param name="required">Whether it must be present.</param>
    /// <param name="errors">Collects errors.</param>
    private static void ValidateSharedQuota(
        string quotaName, QuotaLimit? limit, bool required, OptionsErrorCollector errors)
    {
        if (!required && limit is null)
            return;

        var path = Path(Distributed, quotaName);
        if (!errors.Require(limit, path))
            return;

        if (limit.QueueLimit != 0)
            errors.Add(path, "QueueLimit must be zero; shared quotas do not queue requests.");
    }

    /// <summary>
    /// Checks route policy names and settings.
    /// </summary>
    /// <param name="policies">The policies.</param>
    /// <param name="errors">Collects errors.</param>
    private static void ValidatePolicies(
        IReadOnlyDictionary<string, RouteRateLimitPolicyOptions> policies, OptionsErrorCollector errors)
    {
        foreach (var (policyName, policy) in policies)
        {
            var path = Path(Policies, policyName);
            if (!PolicyName.IsMatch(policyName))
                errors.Add(path, "name may only contain letters, digits, '.', '_' and '-'.");

            errors.Require(policy, path);
        }
    }
}
