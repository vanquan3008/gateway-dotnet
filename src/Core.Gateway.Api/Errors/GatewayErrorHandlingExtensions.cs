using Core.Gateway.Api.Headers;
using Microsoft.AspNetCore.WebUtilities;

namespace Core.Gateway.Api.Errors;

public static class GatewayErrorHandlingExtensions
{
    /// <summary>
    /// Adds problem details that include the correlation id.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGatewayErrorHandling(this IServiceCollection services) =>
        services.AddProblemDetails(
            options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.Request.Headers[GatewayHeaders.CorrelationId].ToString()
        );

    /// <summary>
    /// Turns unhandled exceptions and empty error status codes into problem responses.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder.</returns>
    public static IApplicationBuilder UseGatewayErrorHandling(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages(
            async statusCodeContext =>
            {
                var response = statusCodeContext.HttpContext.Response;
                await Results.Problem(
                        statusCode: response.StatusCode,
                        title: ReasonPhrases.GetReasonPhrase(response.StatusCode),
                        extensions: new Dictionary<string, object?>
                        {
                            ["correlationId"] = statusCodeContext.HttpContext.Request.Headers[GatewayHeaders.CorrelationId].ToString()
                        })
                    .ExecuteAsync(statusCodeContext.HttpContext);
            });

        return app;
    }
}
