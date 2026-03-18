# KillSwitchTraceIdLeak

Minimal reproduction of a **TraceId leak** introduced by the MassTransit kill switch when it restarts a Kafka Rider consumer endpoint.

---

## Prerequisites

| Tool | Version |
|------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 10.0+ |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or Docker Engine + Compose plugin) | any recent |

---

## Quick start

### 1 — Start infrastructure

From the the folder that contains `docker-compose.yml`:

```bash
docker compose up -d
```

This starts:

| Container | Host port | Purpose |
|-----------|-----------|---------|
| `kafka` | `29093` | Kafka broker (KRaft, no ZooKeeper) — apache/kafka |
| `seq` | `5341` | Seq log server — UI, HTTP API and OTLP ingestion |

Wait for Kafka to become healthy (the healthcheck runs `kafka-topics.sh --list` every 10 s, up to 10 retries):

```bash
docker compose ps
```

### 2 — Run the demo

```bash
dotnet run --project KillSwitchTraceIdLeak
```

The app will create the Kafka topic on first run and exit automatically once all phases are complete.

---

## What the demo does

The publisher runs three phases:

| Phase | Count | Description |
|-------|-------|-------------|
| 1 — healthy | 5 | Warms up the kill-switch fault counter |
| 2 — fault | 3 | Trips the kill switch (endpoint stops and schedules a restart) |
| 3 — healthy | 50 | Sent while the endpoint is restarting; makes the leak visible |

All values can be adjusted in DemoConfig.cs.

**Without the workaround** every Phase 3 log line in the console and in Seq shares the same `traceId` — the one leaked from the Activity that was ambient when the kill switch called `Task.Run` to schedule the restart.

**With the workaround** every message gets its own fresh `traceId`.

### Toggling the workaround

Open `KillSwitchTraceIdLeak/Program.cs` and find the `#region workaround` block. Uncomment the `ActivityListener` registration to apply the fix; comment it out again to see the leak:

```csharp
#region workaround
using var leakWorkaround = new ActivityListener
{
    ShouldListenTo = source => source.Name is "MassTransit",
    Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
    {
        if (Activity.Current is { Duration.Ticks: > 0 })
            Activity.Current = null;
        return ActivitySamplingResult.PropagationData;
    }
};
ActivitySource.AddActivityListener(leakWorkaround);
#endregion
```

Restart the app after each toggle. Every run creates a unique consumer group so no messages from the previous run are replayed.

### Viewing traces in Seq

Open **http://localhost:5341** in a browser. Filter by `service.name = 'KillSwitchTraceIdLeak'`.
Without the workaround you will see dozens of Phase 3 events collapsed under a single trace — the leaked one.


## What the workaround does and its trade-offs

`ActivityListener.Sample` fires **synchronously inside the poll loop's own async context** right before MassTransit creates each new receive `Activity`. A stopped `Activity` in `Activity.Current` (`Duration.Ticks > 0`) is the unambiguous signal — running Activities always report `Duration == TimeSpan.Zero`. Nulling it breaks the implicit parent chain before MassTransit reads it.

The correct upstream fix is in MassTransit itself: wrapping `Task.Run(TripAsync)` with `ExecutionContext.SuppressFlow()` so the restarted poll loop inherits a clean context.


### Mechanism

When MassTransit creates a receive `Activity` with no explicit parent (i.e. the message carries no W3C `traceparent` header), .NET's `ActivitySource.StartActivity` calls registered `Sample` callbacks **synchronously**, then reads `Activity.Current` again inside `Activity.Create` to resolve the implicit parent. The workaround registers a listener that fires at exactly that point.

Inside the `Sample` callback it checks whether `Activity.Current` is a **stopped** activity (`Duration.Ticks > 0` — a running activity always reports `Duration == TimeSpan.Zero`). A stopped activity in `Activity.Current` is the unambiguous signal that the context is stale: the leaked `Activity_A` has already completed but its identity is still present in the captured `ExecutionContext`. Nulling `Activity.Current` here happens before `Activity.Create` reads it, so the new receive span finds no parent and becomes a fresh root with a clean `TraceId`.

## Teardown

```bash
docker compose down -v
```

The `-v` flag removes the named volumes (`kafka_data`, `seq_data`) so the next run starts fresh.
