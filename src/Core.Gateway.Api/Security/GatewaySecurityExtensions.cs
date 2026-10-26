using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace Core.Gateway.Api.Security;

public static class GatewaySecurityExtensions
{
    /// <summary>
    /// Registers and validates host filtering, forwarded headers and Kestrel limits; production needs real trusted proxies and restricted hosts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="requireProductionConfiguration">True to enforce the production rules.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewaySecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireProductionConfiguration = false)
    {
        var options = services.AddOptions<GatewaySecurityOptions>()
            .Bind(configuration.GetSection(GatewaySecurityOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.KnownProxies.All(ip => IPAddress.TryParse(ip, out _)), "GatewaySecurity: Invalid trusted proxy IP address.")
            .Validate(o => o.KnownNetworks.All(net => System.Net.IPNetwork.TryParse(net, out _)), "GatewaySecurity: Invalid trusted network CIDR.")
            .Validate(o => o.AllowedHosts.All(host => !string.IsNullOrWhiteSpace(host)), "GatewaySecurity: AllowedHosts contains an empty host.")
            .Validate(o => o.AllowedForwardedHosts.All(host => !string.IsNullOrWhiteSpace(host)), "GatewaySecurity: AllowedForwardedHosts contains an empty host.")
            .Validate(o => o.ForwardLimit > 0, "GatewaySecurity: ForwardLimit must be greater than zero.")
            .Validate(o => o.MaxRequestBodySizeBytes > 0, "GatewaySecurity: MaxRequestBodySizeBytes must be greater than zero.");

        if (requireProductionConfiguration)
        {
            options.Validate(o => o.KnownProxies.Length > 0 || o.KnownNetworks.Length > 0, "GatewaySecurity:KnownProxies or KnownNetworks are required.")

                .Validate(o => o.KnownNetworks.All(net => System.Net.IPNetwork.TryParse(net, out var network) && network.PrefixLength > 0), "GatewaySecurity: Trust-all networks are not allowed.")

                .Validate(o => !o.KnownProxies.Any(GatewaySecurityValidators.IsDocumentationAddress) && !o.KnownNetworks.Any(GatewaySecurityValidators.IsDocumentationNetwork), "GatewaySecurity: Replace documentation sample ranges with real ingress addresses.")

                .Validate(o => o.AllowedHosts.Length > 0 && o.AllowedHosts.All(GatewaySecurityValidators.IsValidRestrictedHost), "GatewaySecurity:AllowedHosts must be restricted.")
                .Validate(o => o.AllowedForwardedHosts.Length > 0 && o.AllowedForwardedHosts.All(GatewaySecurityValidators.IsValidRestrictedHost), "GatewaySecurity:AllowedForwardedHosts must be restricted.");
        }

        options.ValidateOnStart();

        services.AddHostFiltering(_ => { });
        services.AddOptions<HostFilteringOptions>()
            .Configure<IOptions<GatewaySecurityOptions>>((hostFiltering, securityOptions) =>
            {
                hostFiltering.AllowEmptyHosts = false;
                hostFiltering.AllowedHosts.Clear();
                foreach (var host in securityOptions.Value.AllowedHosts)
                {
                    hostFiltering.AllowedHosts.Add(host);
                }
            });

        services.AddOptions<KestrelServerOptions>()
            .Configure<IOptions<GatewaySecurityOptions>>((kestrel, securityOptions) =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = securityOptions.Value.MaxRequestBodySizeBytes;
            });

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<GatewaySecurityOptions>>((forwarded, securityOptions) =>
            {
                var security = securityOptions.Value;
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                forwarded.ForwardLimit = security.ForwardLimit;
                forwarded.RequireHeaderSymmetry = true;
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                forwarded.AllowedHosts.Clear();
                foreach (var proxy in security.KnownProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var network in security.KnownNetworks)
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }

                foreach (var host in security.AllowedForwardedHosts)
                {
                    forwarded.AllowedHosts.Add(host);
                }
            });

        return services;
    }
}
