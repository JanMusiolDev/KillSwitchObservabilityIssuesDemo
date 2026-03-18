using Confluent.Kafka;
using Confluent.Kafka.Admin;
using KillSwitchTraceIdLeak;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Sinks.OpenTelemetry;
using SerilogTracing;

Log.Logger = CreateLogger();

using var _ = new ActivityListenerConfiguration()
    .Instrument.AspNetCoreRequests()
    .TraceToSharedLogger();

#region workaround
//comment/uncomment following code block to see the difference in Seq's UI/console.
//With the workaround in place, the kill switch will still trip and restart the consumer as expected,
//but the TraceId leak will be prevented and you'll see clean TraceIds for all messages in Seq.
//not sure of all sideeffects of this workaround

//using var leakWorkaround = new ActivityListener
//{
//    ShouldListenTo = source => source.Name is "MassTransit",
//    Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
//    {
//        if (Activity.Current is { Duration.Ticks: > 0 })
//            Activity.Current = null;
//        return ActivitySamplingResult.PropagationData;
//    }
//};
//ActivitySource.AddActivityListener(leakWorkaround);
#endregion

await ConfigureKafkaTopic();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((_, services) =>
    {
        services.AddMassTransit(x =>
        {
            x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));

            x.AddRider(rider =>
            {
                rider.AddConsumer<DemoConsumer>();

                rider.UsingKafka((ctx, kafka) =>
                {
                    kafka.Host(DemoConfig.BootstrapServers);

                    kafka.TopicEndpoint<DemoMessage>(
                        DemoConfig.TopicName,
                        DemoConfig.ConsumerGroupId,
                        ep =>
                        {
                            ep.AutoOffsetReset = AutoOffsetReset.Latest;
                            ep.UseKillSwitch(opts =>
                            {
                                opts.SetActivationThreshold(DemoConfig.KsActivationThreshold);
                                opts.SetTripThreshold(DemoConfig.KsTripThreshold);
                                opts.SetRestartTimeout(s: DemoConfig.KsRestartSeconds);
                                opts.SetTrackingPeriod(TimeSpan.FromSeconds(30));
                            });

                            ep.ConfigureConsumer<DemoConsumer>(ctx);
                        });
                });
            });
        });

        services.AddHostedService<PublisherService>();
    })
    .Build();

await host.RunAsync();

static Logger CreateLogger()
{
    var loggerConfig = new LoggerConfiguration()
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate:
                "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj} | traceId={TraceId} spanId={SpanId}{NewLine}{Exception}")
    // The OpenTelemetry sink is optional — the leak can be observed in the console output alone
    .WriteTo.OpenTelemetry(opts =>
    {
        // Seq ingests OTLP logs on this endpoint when the OTLP app is enabled.
        // originaly used opentelemetry exporter endpoint with port 4318
        // switched to Seq's OTLP endpoint to demonstrate the leak more clearly in Seq's UI.
        opts.Endpoint = "http://localhost:5341/ingest/otlp/v1/logs";
        opts.Protocol = OtlpProtocol.HttpProtobuf;
        opts.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = "KillSwitchTraceIdLeak"
            //other properties
        };
    })
    ;

    return loggerConfig.CreateLogger();
}

static async Task ConfigureKafkaTopic()
{
    using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = DemoConfig.BootstrapServers }).Build())
    {
        try
        {
            await admin.CreateTopicsAsync(
            [
                new TopicSpecification
            {
                Name              = DemoConfig.TopicName,
                NumPartitions     = 1,
                ReplicationFactor = 1
            }
            ]);
            Log.Information("Created Kafka topic '{Topic}'", DemoConfig.TopicName);
        }
        catch (CreateTopicsException ex)
            when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            Log.Information("Kafka topic '{Topic}' already exists", DemoConfig.TopicName);
        }
    }
}