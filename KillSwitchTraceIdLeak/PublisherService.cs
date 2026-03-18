using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace KillSwitchTraceIdLeak;

public sealed class PublisherService(ILogger<PublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give the Kafka rider time to connect and receive partition assignments
        // before the first message is produced.
        await Task.Delay(TimeSpan.FromSeconds(4), ct);

        using var producer = new ProducerBuilder<Null, string>(
            new ProducerConfig { BootstrapServers = DemoConfig.BootstrapServers })
            .Build();

        logger.LogInformation(
            "#PHASE 1: sending {N} good messages to activate the kill switch activation threshold",
            DemoConfig.HealthyMessages);

        var counter = 0;
        for (int i = 0; i < DemoConfig.HealthyMessages; i++)
        {
            await Produce(producer, counter++, false, ct);
            await Task.Delay(600, ct);
        }

        logger.LogInformation(
            "#PHASE 2: sending {N} fault messages to trip the kill switch",
            DemoConfig.FaultMessages);

        for (int i = 0; i < DemoConfig.FaultMessages; i++)
        {
            await Produce(producer, counter++, true, ct);
            await Task.Delay(300, ct);
        }

        logger.LogInformation(
            "#PHASE 3: keep sending good messages");

        for (int i = 0; i < 50; i++)
        {
            await Produce(producer, counter++, false, ct);
            await Task.Delay(600, ct);
        }

        logger.LogInformation("Done publishing");
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Environment.Exit(0);
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static Task<DeliveryResult<Null, string>> Produce(IProducer<Null, string> producer, int index, bool shouldFault, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new DemoMessage(index, shouldFault), _jsonOpts);
        return producer.ProduceAsync(DemoConfig.TopicName, new Message<Null, string> { Value = json }, ct);
    }
}
