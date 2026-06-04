# Phase 3 — Event-Driven Reliability

**Status:** ✅ Complete
**Tag:** `v0.4-phase-3`
**Branch:** `phase-3-event-reliability`
**Sessions:** 2

---

## 🎯 Goal

Survive broker restarts, replays, partial failures, and transient network
faults. The Outbox tables built in Phase 2 finally get *reliable* publishing
to Kafka. Consumers gain manual offset commit, exponential-backoff retries,
and a Dead-Letter Queue. Every external call (HTTP + gRPC) is wrapped in
Polly resilience policies.

---

## ✅ What Was Delivered

### **Step 1 — Event Envelope schema**

`PatientFlow.Contracts.Events.EventEnvelope` is the single shape every
event uses on the wire:

| Field | Purpose |
|---|---|
| `EventId` (`Guid`) | Unique per event instance. **Consumers dedupe on this.** |
| `EventType` (`string`) | "PatientCreated", "BillingCreated", etc. — drives consumer routing |
| `Version` (`string`) | `v1` today. Enables future schema evolution without breaking old consumers |
| `OccurredAt` (`DateTime` UTC) | When the domain event happened. Drives ordering + audit trail |
| `Payload` (`object`) | The actual domain data (JSON object) |
| `CorrelationId` (`string?`) | Optional — traces related events across services. Pairs with Phase 6 tracing |
| `Source` (`string`) | Service that emitted the event |
| `Metadata` (`Dictionary<string,string>?`) | Open-ended — used by the retry consumer to carry `RetryCount`, `LastRetryAt`, etc. |

Typed event classes live in `DomainEvents.cs`:
`PatientCreatedEvent`, `PatientUpdatedEvent`, `PatientDeletedEvent`,
`BillingCreatedEvent`.

### **Step 2 — Kafka producer hardening**

`PatientFlow.Common.Kafka.KafkaProducer` (used by all services that emit
events) is now hardened with production-grade settings:

| Setting | Value | Why |
|---|---|---|
| `Acks` | `Acks.All` | Wait for all in-sync replicas to acknowledge before considering a write done. Prevents loss if leader crashes pre-replication. |
| `EnableIdempotence` | `true` | Broker-side dedupe via per-producer sequence numbers. Retries don't create duplicates. |
| `MessageSendMaxRetries` | `10` | Retry transient produce errors automatically. |
| `RetryBackoffMs` | `100` | Initial retry delay. |
| `MaxInFlight` | `5` | Required ceiling for idempotent producers (otherwise duplicate detection breaks). |
| `LingerMs` | `5` | Wait up to 5ms to batch — small latency cost, big throughput win. |
| `CompressionType` | `Snappy` | Reduce network bandwidth ~2–4x. |
| `RequestTimeoutMs` | `30000` | Reasonable upper bound. |

The producer also:
- Implements `IDisposable` with `Flush(TimeSpan)` to ensure all in-flight
  messages are sent before app shutdown.
- Has `SetErrorHandler` + `SetLogHandler` so broker errors / debug logs
  surface in our ILogger pipeline.
- Patient and Billing services hook `ApplicationStopping` to call `Flush()`
  on graceful shutdown.

### **Step 3 — Manual offset commit on consumers**

`PatientFlow.Common.Kafka.KafkaConsumerBase<TPayload>` (new abstract base
class) replaces the previous ad-hoc consumer pattern.

Key consumer settings:

| Setting | Value | Why |
|---|---|---|
| `EnableAutoCommit` | `false` | Offsets are committed ONLY after successful processing. If the consumer crashes mid-process, the message is redelivered. |
| `AutoOffsetReset` | `Earliest` | New consumer groups start from the beginning. |
| `IsolationLevel` | `ReadCommitted` | Pairs with idempotent producer — only see fully-committed messages. |
| `SessionTimeoutMs` | `45000` | Tuned for our local broker. |
| `MaxPollIntervalMs` | `300000` | Long enough for slow processing without rebalance. |

The base class drives the consume loop, deserializes the envelope, dispatches
to `ProcessMessageAsync(envelope, ct)` — a `Task<bool>` returned by the
concrete subclass. **Returning `true` commits the offset**; `false` means
"retry later" and triggers the retry pipeline.

Plus `PartitionsAssignedHandler` / `PartitionsRevokedHandler` log
rebalance events for ops visibility.

### **Step 4 — Retry topics + Dead-Letter Queue**

The same `KafkaConsumerBase` automatically wires up two extra topics per
consumer:

```
   {topic}        ← main consumer reads
   {topic}-retry  ← failed messages republished here with exponential backoff
   {topic}-dlq    ← terminal failures (exceeded max retries) land here
```

On a processing failure:
1. Message is re-published to `{topic}-retry` with `RetryCount` incremented in `Metadata`
2. `PatientEventsRetryConsumer` reads `{topic}-retry`, sleeps `2^RetryCount` seconds (2s, 4s, 8s), then attempts processing again
3. After `maxRetryAttempts` (default 3), the message goes to `{topic}-dlq` via `SendToDLQAsync`
4. DLQ is poison-message storage — never auto-processed. Operations team investigates.

Deserialize failures (malformed envelope) skip retry and go straight to DLQ
— there's no point retrying garbled JSON.

### **Step 5 — Polly resilience on every external call**

`PatientFlow.Common.Resilience.ResiliencePolicies` exposes four factory
methods using the **modern Polly v8 `ResiliencePipeline` API** (not the
legacy `IAsyncPolicy<T>`):

| Method | Strategy |
|---|---|
| `GetRetryPolicy<T>` | Exponential backoff retries on transient failures |
| `GetCircuitBreakerPolicy<T>` | Open the circuit after N failures, half-open after cooldown |
| `GetTimeoutPolicy<T>` | Fail fast if the call exceeds a deadline |
| `GetCombinedPolicy<T>(timeout)` | All three composed: timeout → retry → circuit breaker |

Applied in:
- **`PMS.Patient/Services/BillingGrpcClient.cs`** — wraps the gRPC call
  to Billing with `GetCombinedPolicy<BillingResponse>(timeout: 5s)`. Transient
  network failures auto-retry, slow Billing trips the breaker, the saga
  transaction in `PatientService.CreatePatientAsync` rolls back on hard failures.
- **`PMS.AI/Services/LLMService.cs`** — wraps the HTTP call to Ollama with
  a `ResiliencePipeline<string>` so transient LLM unavailability auto-retries.

---

## 🏗️ Architecture Decisions

### **1. Generic `KafkaConsumerBase<TPayload>` instead of one-off consumer classes**

The Phase 3 plan didn't call for a base class explicitly — but having it
makes every concern (manual commit, retry, DLQ, partition logging) a
write-once, inherit-many setup. Future consumers in Phase 4+ get all of
this for free by extending the base.

**Trade-off:** the base class is more complex internally (~150 lines vs.
the old ~50). For the second consumer onward, this pays back several
times over.

### **2. Redis-backed deduplication with 7-day TTL**

Consumers check `processed_event:{eventId}` in Redis before processing,
and mark it after success.

**Why Redis vs. a Postgres table:**
- Faster lookup (sub-millisecond)
- Auto-expiration via TTL (no cleanup worker needed)
- One service (AI) consumes our events today; per-service Redis already exists

**Why 7-day TTL:**
- Long enough that any reasonable replay window covers
- Short enough that the dedupe set doesn't grow forever
- Tunable via config later if we need different retention per consumer

### **3. Exponential backoff inline (Task.Delay) rather than topic-level delay**

The retry consumer sleeps inside the message handler using `Task.Delay(2^retry)`.
This blocks the partition for up to 8 seconds during the third retry attempt.

**Pros:** Simple, no extra infrastructure
**Cons:** Holds the partition during delay — limits throughput on heavily
loaded topics

**Why OK for now:** Our event volume is low. For a production system at
scale, you'd use Kafka Streams or a delayed-delivery topic.

### **4. Polly v8 `ResiliencePipeline` over the legacy `IAsyncPolicy<T>`**

The v8 API is the current direction Microsoft + Polly recommend. Slightly
different syntax from the older Polly tutorials online, but cleaner: each
strategy is added to a builder via `.AddX(...)`, then `.Build()` produces
an immutable pipeline.

Combined pipeline order matters — outermost first:
```csharp
new ResiliencePipelineBuilder<T>()
    .AddTimeout(...)        // ← outermost: total time budget for everything below
    .AddRetry(...)          // ← retries each attempt against the circuit breaker
    .AddCircuitBreaker(...) // ← innermost: counts individual failures
    .Build();
```

A retry only fires if the inner call (which may include the circuit
breaker) actually failed. The timeout applies to the whole composition.

### **5. Producer + consumer use the same hardened settings**

The consumer base class instantiates its own producer (for retry/DLQ
publishing). It uses the same `Acks=All` + idempotence + Snappy
compression settings as the standalone `KafkaProducer`. Consistency
across the codebase, no surprises.

---

## ✅ Done When Criteria

| Criterion | Status |
|---|---|
| Outbox relay uses the hardened producer settings | ✅ |
| Kafka producer: `Acks=All`, `EnableIdempotence=true`, flush on shutdown | ✅ |
| Consumers: manual offset commit (`EnableAutoCommit=false`) | ✅ |
| Retry topic + DLQ topic per consumer; exponential backoff | ✅ |
| Event envelope: `EventId`, `EventType`, `Version`, `OccurredAt`, `Payload` (+ bonus fields) | ✅ |
| Consumer dedupe by `EventId` | ✅ |
| Polly resilience on every HTTP/gRPC call (Patient→Billing gRPC, AI→Ollama HTTP) | ✅ |
| Build clean: `dotnet build PatientFlow.sln` → 0 errors, 0 warnings | ✅ |
| Retrospective documented | ✅ (this file) |
| Phase 3 merged into main, tagged `v0.4-phase-3` | (pending merge) |

---

## 📌 Deferred to Later Phases

| Item | Phase |
|---|---|
| Row-level locking on outbox polling (`SELECT ... FOR UPDATE SKIP LOCKED`) for multi-replica safety | Phase 8 |
| Explicit topic creation step (instead of relying on Kafka's `auto.create.topics.enable`) — needed when prod brokers disable that flag | Phase 8 |
| Move retry-delay out of the consumer thread (delayed-delivery topic or Kafka Streams) | Phase 8 / not strictly needed |
| Defensive `int.TryParse` on `RetryCount` Metadata read | Phase 7 cleanup |
| Split `RedisService` into `IEventDedupeStore` + `IPatientContextStore` (single-responsibility) | Phase 6 / 7 |
| OpenTelemetry instrumentation of producer + consumer | Phase 6 |
| `CorrelationId` propagation through service-to-service calls (header-based) | Phase 6 |

---

## 💡 Key Learnings

### **1. Idempotent producer + `MaxInFlight ≤ 5` is a load-bearing constraint**

Kafka's idempotent-producer guarantee requires `max.in.flight.requests.per.connection ≤ 5`.
Set it higher and you silently lose duplicate detection — no error,
no warning, just broken semantics. Always set this explicitly.

### **2. `Acks=All` only guarantees what your replication factor allows**

`Acks=All` means "wait for all in-sync replicas" — but if your topic
has replication-factor 1, that's still just one broker. Production
needs RF ≥ 3 for `Acks=All` to actually buy you durability. Our local
broker is RF=1 for now; Phase 8 (Strimzi 3-broker KRaft cluster) fixes this.

### **3. Manual commit + return-bool pattern is the cleanest consumer abstraction**

Rather than each consumer dealing with `IConsumer.Commit()` directly, the
base class takes the boolean from `ProcessMessageAsync` and either commits
(`true`) or sends to retry/DLQ (`false`). Subclasses become pure business
logic. This pattern is reusable across any future consumer.

### **4. Modern Polly v8 is a small but meaningful upgrade**

If you're learning Polly from blog posts, you'll often see the older
`Policy.WrapAsync(...)` API. v8 (`ResiliencePipelineBuilder<T>`) is the
direction Microsoft + the Polly team are heading. Use the modern API for
new code — the older one still works but is in maintenance mode.

### **5. Exactly-once vs. at-least-once: be honest about what you have**

With Kafka idempotent producer + `Acks=All` + consumer dedupe by EventId,
we have **at-least-once with consumer-side dedupe = effectively exactly-once
for this domain**. True exactly-once across Kafka + a SQL transaction needs
Kafka Streams transactions or a saga orchestrator — both heavier than what
we built. Our approach is the 80/20 sweet spot.

---

## 🚀 What's Next (Phase 4)

**Security hardening.** With reliable event flow now in place, we close
the obvious security holes:

- Refresh token flow + Redis-backed revocation list
- JWT `kid` header + key rotation strategy
- Account lockout after N failed logins
- Partitioned rate limiter keyed on IP + email (not global)
- Security headers middleware (HSTS, CSP, etc.)
- Strict error responses (no `exception.Message` leaks to clients)
- **Audit log table** — every PHI access logged (HIPAA §164.312(b))
- Non-root container `USER`

Phase 3 built infrastructure resilience. Phase 4 builds adversarial
resilience.
