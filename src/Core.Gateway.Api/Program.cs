using Core.Gateway.Api.Authentication;
using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.Configuration.CORS;
using Core.Gateway.Api.Configuration.Security;
using Core.Gateway.Api.Extensions;
using Core.Gateway.Api.HealthChecks;
using Core.Gateway.Api.Observability;
using Core.Gateway.Api.RateLimiting;
using Core.Gateway.Api.Resilience;
using Core.Gateway.Api.Transformations;

var app = Program.CreateApplication(args);
app.Run();

public partial class Program
{
    public static WebApplication CreateApplication(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddGatewayRoutes(args);
        configureBuilder?.Invoke(builder);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.Request.Headers[GatewayHeaders.CorrelationId].ToString())
            .AddGatewaySecurity(builder.Configuration, !builder.Environment.IsDevelopment())
            .AddGatewayCors(builder.Configuration)
            .AddGatewayAuthentication(builder.Configuration, !builder.Environment.IsDevelopment())
            .AddGatewayRateLimiting(builder.Configuration)
            .AddGatewayResilience(builder.Configuration)
            .AddGatewayHealthChecks()
            .AddGatewayObservability(builder.Configuration)
            .AddGatewayReverseProxy(builder.Configuration);

        var app = builder.Build();
        app.UseGatewayMiddleware();
        app.MapGatewayHealthChecks();
        app.MapReverseProxy();
        return app;
    }
}
