using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Configuration.CORS;
using Core.Gateway.Api.Middleware;
using Core.Gateway.Api.RateLimiting.Distributed;
using Core.Gateway.Api.RateLimiting.Local;
using Core.Gateway.Api.Resilience;
using Core.Gateway.Api.Transformations;
using Microsoft.AspNetCore.WebUtilities;

namespace Core.Gateway.Api.Extensions;

public static class GatewayApplicationBuilderExtensions
{
    public static WebApplication UseGatewayMiddleware(
     this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages(async statusCodeContext =>
        {
            var response = statusCodeContext.HttpContext.Response;

            await Results.Problem(
                    statusCode: response.StatusCode,
                    title: ReasonPhrases.GetReasonPhrase(response.StatusCode),
                    extensions: new Dictionary<string, object?>
                    {
                        ["correlationId"] =
                            statusCodeContext.HttpContext.Request
                                .Headers[GatewayHeaders.CorrelationId]
                                .ToString()
                    })
                .ExecuteAsync(statusCodeContext.HttpContext);
        });

        if (app.Environment.IsProduction())
        {
            app.UseHsts();
        }

        app.UseHostFiltering();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseRouting();
        app.UseRequestTimeouts();
        app.UseMiddleware<GatewayForwarderErrorMiddleware>();
        app.UseCors(GatewayCorsExtensions.PolicyName);
        app.UseMiddleware<GatewayConcurrencyLimiterMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<GatewayAuthenticationFailureMiddleware>();
        app.UseRateLimiter();
        app.UseMiddleware<DistributedRateLimitingMiddleware>();
        app.UseAuthorization();

        return app;
    }
}
