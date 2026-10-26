using Core.Gateway.Api.Configuration;
using Core.Gateway.Api.DependencyInjection;
using Core.Gateway.Api.Pipeline;

var app = Program.CreateApplication(args);
app.Run();

public partial class Program
{
    /// <summary>
    /// Builds the gateway app: loads route and environment config, registers every layer and the pipeline. Tests can change the builder first.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configureBuilder">Optional hook to change the builder before it is built.</param>
    /// <returns>The configured app, not started.</returns>
    public static WebApplication CreateApplication(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddGatewayRoutes(args);
        configureBuilder?.Invoke(builder);

        builder.AddCoreGateway();
        var app = builder.Build();
        app.UseCoreGateway();
        return app;
    }
}
