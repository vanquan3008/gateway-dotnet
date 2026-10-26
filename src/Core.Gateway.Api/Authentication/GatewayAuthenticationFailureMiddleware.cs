using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Core.Gateway.Api.Authentication;

internal sealed class GatewayAuthenticationFailureMiddleware(RequestDelegate next)
{
    private static readonly object MetadataUnavailable = new();

    public Task InvokeAsync(HttpContext context)
    {
        return context.Items.ContainsKey(MetadataUnavailable)
            ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service Unavailable", detail: "Authentication metadata is temporarily unavailable.")
                .ExecuteAsync(context)
            : next(context);
    }

    internal static void MarkMetadataUnavailable(MessageReceivedContext context, Exception exception)
    {
        context.HttpContext.Items[MetadataUnavailable] = true;
        context.Fail(exception);
        context.HttpContext.RequestServices.GetRequiredService<ILogger<GatewayAuthenticationFailureMiddleware>>()
            .LogWarning("Authentication metadata unavailable ({Reason}); request fails closed.", exception.GetType().Name);
    }
}
