namespace KillSwitchTraceIdLeak;

//hardcoded minimal config
static class DemoConfig
{
    public const int FaultMessages   = 3;
    public const int HealthyMessages = 5;

    public const int KsActivationThreshold = 5;  
    public const int KsTripThreshold       = 5; 
    public const int KsRestartSeconds      = 10; 

    public const string BootstrapServers = "localhost:29093";
    public const string TopicName        = "kill-switch-traceid-leak-demo";

    // Unique per run so the consumer always starts from the tip of the topic
    // and never replays messages from a previous session.
    public static readonly string ConsumerGroupId =
        $"ks-demo-{DateTime.UtcNow:yyyyMMddHHmmss}";
}
