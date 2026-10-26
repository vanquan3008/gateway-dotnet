using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Authentication.Middleware;
using Core.Gateway.Api.Correlation;
using Core.Gateway.Api.Errors;
using Core.Gateway.Api.HealthChecks;
using Core.Gateway.Api.Observability;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.RateLimiting.Routes;
using Core.Gateway.Api.Resilience;
using Core.Gateway.Api.Security;
using Core.Gateway.Api.Security.Cors;
using Core.Gateway.Api.Security.Waf;

namespace Core.Gateway.Api.Pipeline;

public static class GatewayPipelineExtensions
{
    /// <summary>
    /// Builds the middleware pipeline in order and maps the reverse proxy.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application.</returns>
    public static WebApplication UseCoreGateway(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseGatewayErrorHandling();
        app.UseResponseCompression();
        if (app.Environment.IsProduction())
        {
            app.UseHsts();
        }

        app.UseHostFiltering();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<GatewaySanitizationMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();

        app.UseRouting();
        app.UseGatewayManagementPort();
        app.UseGatewayRequestLimits();
        app.UseRequestTimeouts();
        app.UseMiddleware<GatewayForwarderErrorMiddleware>();
        app.UseCors(GatewayCorsExtensions.PolicyName);

        app.UseMiddleware<GatewayConcurrencyLimiterMiddleware>();
        app.UseRateLimiter();

        app.UseAuthentication();
        app.UseMiddleware<GatewayAuthenticationFailureMiddleware>();
        app.UseMiddleware<GatewayAnonymousRateLimitingMiddleware>();
        app.UseMiddleware<GatewayRouteRateLimitingMiddleware>();
        app.UseMiddleware<DistributedRateLimitingMiddleware>();
        app.UseAuthorization();

        app.MapGatewayHealthChecks();
        app.MapReverseProxy(proxy =>
        {
            proxy.UseGatewayRetries();
            proxy.UseSessionAffinity();
            proxy.UseLoadBalancing();
            proxy.UsePassiveHealthChecks();
        });
        return app;
    }
}
