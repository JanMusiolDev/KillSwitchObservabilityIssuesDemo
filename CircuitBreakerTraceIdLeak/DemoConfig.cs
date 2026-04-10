using Confluent.Kafka;

namespace CircuitBreakerTraceIdLeak;

//hardcoded minimal config
static class DemoConfig
{
    public const int FaultMessages = 3;
    public const int HealthyMessages = 5;

    public const int CbActivationThreshold = 5;
    public const int CbTripThreshold = 20;
    public const int CbRestartSeconds = 10;

    public const string BootstrapServers = "localhost:29093";
    public const string TopicName = "circuit-breaker-traceid-leak-demo";

    public static readonly string ConsumerGroupId =
        $"cb-demo-{DateTime.UtcNow:yyyyMMddHHmmss}";
}
