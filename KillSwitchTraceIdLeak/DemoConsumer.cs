using MassTransit;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace KillSwitchTraceIdLeak;

// Consumer — the place where the TraceId leak becomes directly observable.
public sealed class DemoConsumer(ILogger<DemoConsumer> logger)
    : IConsumer<DemoMessage>
{
    private static readonly ActivitySource _activitySource = new("DemoService");

    public async Task Consume(ConsumeContext<DemoMessage> ctx)
    {
        using var span = _activitySource.StartActivity(nameof(Consume), ActivityKind.Consumer);

        var mtActivity = Activity.Current?.Parent ?? Activity.Current; // MassTransit span
        var traceId = mtActivity?.TraceId.ToString() ?? "(none)";
        var spanId = mtActivity?.SpanId.ToString() ?? "(none)";

        logger.LogInformation("MSG #{Index:D3} START", ctx.Message.Index);

        if (ctx.Message.ShouldFault)
        {
            // Simulate the async work
            await Task.Delay(10, ctx.CancellationToken);
            throw new InvalidOperationException($"Deliberate fault on message #{ctx.Message.Index} — tripping kill switch.");
        }

        logger.LogInformation("MSG #{Index:D3} END", ctx.Message.Index);
    }
}
