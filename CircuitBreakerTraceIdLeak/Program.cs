using Confluent.Kafka;
using Confluent.Kafka.Admin;
using JasperFx.Core;
using CircuitBreakerTraceIdLeak;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using SerilogTracing;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Kafka;
using Serilog.Sinks.OpenTelemetry;

Log.Logger = CreateLogger();

using var _ = new ActivityListenerConfiguration()
    .Instrument.AspNetCoreRequests()
    .TraceToSharedLogger();

await ConfigureKafkaTopic();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .UseWolverine()
    .ConfigureServices((_, services) =>
    {
        services.AddSingleton<ConsumptionTracker>();
        services.AddWolverineExtension<KafkaWolverineExtension>();

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
            ["service.name"] = "CircuitBreakerTraceIdLeak"
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
            //check if topic exixts, if it does - empty it, if not - create it
            var desc = await admin.DescribeTopicsAsync(TopicCollection.OfTopicNames([DemoConfig.TopicName]));
            if (desc.TopicDescriptions.Count > 0)
            {
                await admin.DeleteTopicsAsync([DemoConfig.TopicName]);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "could not delete Kafka topic - might not exist");

        }

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

public class KafkaWolverineExtension() : IWolverineExtension
{
    public void Configure(WolverineOptions opts)
    {
        opts.UseKafka(DemoConfig.BootstrapServers)
        .ConfigureClient((cfg) => { })
        .ConsumeOnly();

        opts.ListenToKafkaTopic(DemoConfig.TopicName)
            .ProcessInline()
            .ReceiveRawJson<DemoMessage>()
            .ConfigureConsumer(consumer =>
            {
                consumer.GroupId = DemoConfig.ConsumerGroupId;
                consumer.AutoOffsetReset = AutoOffsetReset.Earliest;
            });

        // Configure circuit breaker and custom serializer on Kafka endpoint
        opts.Policies.Add(new LambdaEndpointPolicy<KafkaTopic>((endpoint, runtime) =>
        {
            //endpoint.DefaultSerializer = runtime.Services.GetRequiredService<LoggingJsonSerializer>();

            endpoint.CircuitBreakerOptions = new CircuitBreakerOptions
            {
                TrackingPeriod = 30.Seconds(),
                MinimumThreshold = DemoConfig.CbActivationThreshold,
                FailurePercentageThreshold = DemoConfig.CbTripThreshold,
                PauseTime = DemoConfig.CbRestartSeconds.Seconds(),
                SamplingPeriod = 100.Milliseconds()
            };
        }));

        opts.Policies.DisableConventionalLocalRouting();
    }

}