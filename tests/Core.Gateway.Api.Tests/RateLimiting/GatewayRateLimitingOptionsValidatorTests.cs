using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Distributed.Fallback;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.RateLimiting.Routes;
using Core.Gateway.Api.RateLimiting.Shared;
using Microsoft.Extensions.Options;
using Xunit;

namespace Core.Gateway.Api.Tests.RateLimiting;

public sealed class GatewayRateLimitingOptionsValidatorTests
{
    private static readonly GatewayRateLimitingOptionsValidator Validator = new();

    [Fact]
    public void Defaults_AreValid()
    {
        var result = Validator.Validate(null, new GatewayRateLimitingOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidPolicyAndOptionalQuotas_AreAccepted()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = new LocalRateLimitingOptions { Anonymous = new QuotaLimit { PermitLimit = 10 } },
            Distributed = new DistributedRateLimitingOptions
            {
                Tenant = new QuotaLimit { PermitLimit = 100 },
                ApiKey = new QuotaLimit { PermitLimit = 100 }
            },
            Policies = { ["strict-1.v2_x"] = new RouteRateLimitPolicyOptions { PermitLimit = 5 } }
        };

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void MissingSections_AreReportedByPath()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = null!,
            Distributed = new DistributedRateLimitingOptions { Authenticated = null!, Fallback = null! }
        };

        var failures = Failures(options);

        Assert.Contains("Local is required.", failures);
        Assert.Contains("Distributed:Authenticated is required.", failures);
        Assert.Contains("Distributed:Fallback is required.", failures);
    }

    [Fact]
    public void MissingLocalQuotas_AreReportedByPath()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = new LocalRateLimitingOptions { Instance = null!, Service = null! }
        };

        var failures = Failures(options);

        Assert.Contains("Local:Instance is required.", failures);
        Assert.Contains("Local:Service is required.", failures);
    }

    [Fact]
    public void NestedAnnotations_AreReportedUnderTheirPath()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = new LocalRateLimitingOptions
            {
                Instance = new QuotaLimit { PermitLimit = 0 },
                Anonymous = new QuotaLimit { PermitLimit = 0 }
            },
            Distributed = new DistributedRateLimitingOptions
            {
                Tenant = new QuotaLimit { PermitLimit = 0 },
                Fallback = new DistributedFallbackOptions { LocalRatio = 0 }
            }
        };

        var failures = Failures(options);

        Assert.Contains(failures, f => f.StartsWith("Local:Instance: ", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Local:Anonymous: ", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Distributed:Tenant: ", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Distributed:Fallback: ", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceConcurrencyAboveInstanceConcurrency_IsRejected()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = new LocalRateLimitingOptions { InstanceConcurrencyLimit = 10, ServiceConcurrencyLimit = 11 }
        };

        Assert.Equal(
            ["Local:ServiceConcurrencyLimit must not exceed Local:InstanceConcurrencyLimit."],
            Failures(options));
    }

    [Fact]
    public void ServiceConcurrencyEqualToInstanceConcurrency_IsAccepted()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = new LocalRateLimitingOptions { InstanceConcurrencyLimit = 10, ServiceConcurrencyLimit = 10 }
        };

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("Authenticated")]
    [InlineData("Anonymous")]
    [InlineData("Tenant")]
    [InlineData("ApiKey")]
    public void SharedQuotaThatQueues_IsRejected(string quota)
    {
        var queued = new QuotaLimit { PermitLimit = 10, QueueLimit = 1 };
        var options = new GatewayRateLimitingOptions
        {
            Distributed = new DistributedRateLimitingOptions
            {
                Authenticated = quota == "Authenticated" ? queued : new QuotaLimit { PermitLimit = 10 },
                Anonymous = quota == "Anonymous" ? queued : new QuotaLimit { PermitLimit = 10 },
                Tenant = quota == "Tenant" ? queued : null,
                ApiKey = quota == "ApiKey" ? queued : null
            }
        };

        Assert.Equal(
            [$"Distributed:{quota}: QueueLimit must be zero; shared quotas do not queue requests."],
            Failures(options));
    }

    [Fact]
    public void LocalQuotasMayQueue()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = new LocalRateLimitingOptions { Instance = new QuotaLimit { PermitLimit = 10, QueueLimit = 5 } }
        };

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData("with/slash")]
    public void PolicyNameWithForbiddenCharacters_IsRejected(string policyName)
    {
        var options = new GatewayRateLimitingOptions
        {
            Policies = { [policyName] = new RouteRateLimitPolicyOptions { PermitLimit = 5 } }
        };

        Assert.Equal(
            [$"Policies:{policyName}: name may only contain letters, digits, '.', '_' and '-'."],
            Failures(options));
    }

    [Fact]
    public void PolicyNameLongerThan64Characters_IsRejected()
    {
        var policyName = new string('a', 65);
        var options = new GatewayRateLimitingOptions
        {
            Policies = { [policyName] = new RouteRateLimitPolicyOptions { PermitLimit = 5 } }
        };

        Assert.Single(Failures(options));
        Assert.True(Validator.Validate(null, new GatewayRateLimitingOptions
        {
            Policies = { [new string('a', 64)] = new RouteRateLimitPolicyOptions { PermitLimit = 5 } }
        }).Succeeded);
    }

    [Fact]
    public void PolicyWithInvalidValues_IsReportedUnderItsName()
    {
        var options = new GatewayRateLimitingOptions
        {
            Policies =
            {
                ["strict"] = new RouteRateLimitPolicyOptions { PermitLimit = 0 },
                ["missing"] = null!
            }
        };

        var failures = Failures(options);

        Assert.Contains(failures, f => f.StartsWith("Policies:strict: ", StringComparison.Ordinal));
        Assert.Contains("Policies:missing is required.", failures);
    }

    [Fact]
    public void AllProblemsAreReportedAtOnce()
    {
        var options = new GatewayRateLimitingOptions
        {
            Local = new LocalRateLimitingOptions { InstanceConcurrencyLimit = 1, ServiceConcurrencyLimit = 2 },
            Distributed = new DistributedRateLimitingOptions
            {
                Tenant = new QuotaLimit { PermitLimit = 10, QueueLimit = 1 }
            },
            Policies = { ["bad name"] = new RouteRateLimitPolicyOptions { PermitLimit = 5 } }
        };

        Assert.Equal(3, Failures(options).Count);
    }

    private static IReadOnlyList<string> Failures(GatewayRateLimitingOptions options)
    {
        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        return result.Failures!.ToList();
    }
}
