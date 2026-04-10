using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CircuitBreakerTraceIdLeak;

// Consumer — the place where the TraceId leak becomes directly observable.
public sealed class DemoConsumer(ILogger<DemoConsumer> logger, ConsumptionTracker tracker)
{
    private static readonly ActivitySource _activitySource = new("DemoService");

    public async Task ConsumeAsync(DemoMessage message, CancellationToken cancellationToken)
    {
        using var span = _activitySource.StartActivity(nameof(ConsumeAsync), ActivityKind.Consumer);

        var mtActivity = Activity.Current?.Parent ?? Activity.Current;
        var traceId = mtActivity?.TraceId.ToString() ?? "(none)";
        var spanId = mtActivity?.SpanId.ToString() ?? "(none)";

        logger.LogInformation("MSG #{Index:D3} START", message.Index);

        if (message.ShouldFault)
        {
            // Simulate the async work
            await Task.Delay(10, cancellationToken);
            throw new InvalidOperationException($"Deliberate fault on message #{message.Index} — tripping circuit breaker.");
        }

        logger.LogInformation("MSG #{Index:D3} END", message.Index);
        tracker.MessageProcessed(message.Index);
    }
}
