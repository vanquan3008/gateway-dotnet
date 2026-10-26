using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Xunit;

namespace Core.Gateway.Api.Tests.Integration;

public sealed partial class GatewayPipelineTests
{
    [Fact]
    public async Task CollectorTimeout_DoesNotBlockRequestsOrHealthAndConsoleQueueCanDropDiagnostics()
    {
        using var collector = new BlockedCollectorHandler();
        using var source = new ActivitySource($"gateway-test-{Guid.NewGuid():N}");
        await using var host = await GatewayTestHost.StartAsync(new()
        {
            ["Observability:OtlpExporterEnabled"] = "true",
            ["Observability:OtlpEndpoint"] = "https://collector.test",
            ["Observability:OtlpTimeoutMilliseconds"] = "200"
        }, configureGateway: builder =>
        {
            builder.Services.PostConfigureAll<OtlpExporterOptions>(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.HttpClientFactory = () => new HttpClient(collector, disposeHandler: false)
                {
                    Timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds)
                };
            });
            builder.Services.AddOpenTelemetry().WithTracing(tracing =>
                tracing.AddSource(source.Name).SetSampler(new AlwaysOnSampler()));
        });
        var console = host.Services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
        Assert.Equal(ConsoleLoggerQueueFullMode.DropWrite, console.QueueFullMode);
        Assert.Equal(1024, console.MaxQueueLength);
        using var first = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/users"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using (var activity = source.StartActivity("collector timeout check"))
        {
            Assert.NotNull(activity);
        }
        var provider = host.Services.GetRequiredService<TracerProvider>();
        var flush = Task.Run(() => provider.ForceFlush(2_000));
        try
        {
            await collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var next = await host.Client.SendAsync(host.AuthenticatedRequest(HttpMethod.Get, "/api/orders"));
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            using var ready = await host.Client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            await collector.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            collector.Release.TrySetResult();
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class BlockedCollectorHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken).GetAwaiter().GetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }
}
