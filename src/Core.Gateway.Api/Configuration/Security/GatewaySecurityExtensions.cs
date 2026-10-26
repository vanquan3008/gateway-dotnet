using System.Net;
using Core.Gateway.Api.Shares;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Configuration.Security;

public static class GatewaySecurityExtensions
{
    public static IServiceCollection AddGatewaySecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireProductionConfiguration = false)
    {
        var section = configuration.GetSection(GatewaySecurityOptions.SectionName);

        services.AddOptions<GatewaySecurityOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .Validate(
                options => options.KnownProxies.All(ip => IPAddress.TryParse(ip, out _)),
                "GatewaySecurity:KnownProxies contains an invalid IP address.")
            .Validate(
                options => options.KnownNetworks.All(net => System.Net.IPNetwork.TryParse(net, out _)),
                "GatewaySecurity:KnownNetworks contains an invalid CIDR network.")
            .Validate(
                options => !requireProductionConfiguration || options.KnownNetworks.All(net =>
                    System.Net.IPNetwork.TryParse(net, out var network) && network.PrefixLength > 0),
                "GatewaySecurity:KnownNetworks must not trust all addresses outside Development.")
            .Validate(
                options => !requireProductionConfiguration ||
                           options.KnownProxies.Length > 0 ||
                           options.KnownNetworks.Length > 0,
                "GatewaySecurity:KnownProxies or KnownNetworks is required in Production.")
            .Validate(
                options => !requireProductionConfiguration ||
                           !options.KnownProxies.Any(GatewaySecurityValidators.IsDocumentationAddress) &&
                           !options.KnownNetworks.Any(GatewaySecurityValidators.IsDocumentationNetwork),
                "GatewaySecurity:KnownProxies/KnownNetworks still use a documentation sample range; set the real ingress addresses.")
            .Validate(
                options => !requireProductionConfiguration || (options.AllowedForwardedHosts.Length > 0 &&
                            options.AllowedForwardedHosts.All(GatewaySecurityValidators.IsValidRestrictedHost)),
                "GatewaySecurity:AllowedForwardedHosts must be restricted in Production.")
            .Validate(
                options => options.AllowedHosts.All(host => !string.IsNullOrWhiteSpace(host)),
                "GatewaySecurity:AllowedHosts contains an empty host.")
            .Validate(
                options => !requireProductionConfiguration || options.AllowedHosts.All(GatewaySecurityValidators.IsValidRestrictedHost),
                "GatewaySecurity:AllowedHosts must be restricted in Production.")
            .ValidateOnStart();

        services.AddOptions<HostFilteringOptions>()
            .Configure<IOptions<GatewaySecurityOptions>>((options, securityOptions) =>
            {
                var security = securityOptions.Value;

                options.AllowEmptyHosts = false;
                options.AllowedHosts.Clear();
                foreach (var host in security.AllowedHosts)
                {
                    options.AllowedHosts.Add(host);
                }
            });

        services.AddHostFiltering(_ => { });
        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<GatewaySecurityOptions>>((options, securityOptions) =>
            {
                var security = securityOptions.Value;

                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = security.MaxRequestBodySizeBytes;
            });
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<GatewaySecurityOptions>>((options, securityOptions) =>
            {
                var security = securityOptions.Value;

                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor |
                    ForwardedHeaders.XForwardedProto |
                    ForwardedHeaders.XForwardedHost;

                options.ForwardLimit = security.ForwardLimit;
                options.RequireHeaderSymmetry = true;

                if (security.KnownProxies.Length > 0 || security.KnownNetworks.Length > 0)
                {
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                }

                foreach (var proxy in security.KnownProxies)
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in security.KnownNetworks)
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }

                options.AllowedHosts.Clear();
                foreach (var host in security.AllowedForwardedHosts)
                {
                    options.AllowedHosts.Add(host);
                }
            });

        return services;
    }
}
