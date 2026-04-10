# Wolverine Circuit Breaker — Observability Issues Demo

Minimal reproduction repository demonstrating several observability and behavioural issues with the Wolverine **Circuit Breaker** policy when used with a Kafka transport.

---

## Issues Demonstrated

### 1. TraceId Leak After Circuit Breaker Restart (Main Issue)

After the circuit breaker trips, pauses, and then **restarts** the Kafka consumer, **every subsequent message is processed under the same `traceId`**. All log entries share one stale trace id.

**Expected:** each consumed message should start a new trace with a unique `traceId`.

### 2. Circuit Breaker Does Not Trip Instantly at Threshold

The circuit breaker does not react to failures immediately upon reaching the configured failure-percentage threshold.
Assumption: Instead, it evaluates on a timer (`SamplingPeriod`), so there is a delay between the threshold being crossed and the breaker actually opening.
It might allow higher than intended failure rates.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker / Docker Compose](https://docs.docker.com/get-docker/)

## How to Run

### 1. Start Infrastructure

From the `CircuitBreakerTraceIdLeak` directory, start Kafka and (optionally) Seq:

```bash
docker compose up -d
```

This starts:

| Service | Port | Purpose |
|---------|------|---------|
| **Kafka** | `localhost:29093` | Message broker |
| **Seq** | `localhost:5341` | Log viewer (optional — the leak is visible in console output alone) |

### 2. Run the Application

```bash
cd CircuitBreakerTraceIdLeak
dotnet run
```

### 3. Observe the Output

The application runs through three phases automatically:

| Phase | What Happens |
|-------|--------------|
| **Phase 1** | Publishes **5 healthy messages** to activate the circuit breaker's minimum-threshold counter. |
| **Phase 2** | Publishes **3 fault messages** that throw exceptions, pushing the failure percentage above the trip threshold. |
| **Phase 3** | Publishes **50 healthy messages** after the circuit breaker pauses and restarts the consumer. |

Watch the console output. During Phase 3 you will see that the `traceId` logged for every message is **identical** — it is the stale trace from the circuit breaker's internal pause/restart activity, not a fresh per-message trace.

```
[HH:mm:ss.fff INF] MSG #008 START | traceId=<same-id> spanId=...
[HH:mm:ss.fff INF] MSG #008 END   | traceId=<same-id> spanId=...
[HH:mm:ss.fff INF] MSG #009 START | traceId=<same-id> spanId=...
[HH:mm:ss.fff INF] MSG #009 END   | traceId=<same-id> spanId=...
```

If Seq is running, open `http://localhost:5341` to see all post-restart messages grouped under a single trace.

## Cleanup

```bash
docker compose down -v
```
