# Phase 3: Event-Driven Reliability — Retrospective

**Branch:** `phase-3-event-reliability`  
**Duration:** 2 sessions  
**Commits:** 5  
**Tag:** `v0.4-phase-3`  
**Merged to main:** [Date TBD]

---

## 🎯 Phase Objectives (Achieved)

Make the event-driven architecture **bulletproof**:
- ✅ Events survive Kafka broker restarts
- ✅ No duplicate processing across replays
- ✅ Transient network failures auto-recover
- ✅ Poison messages don't block healthy traffic
- ✅ Cascading failures prevented

---

## 📦 What We Delivered

### Step 1: Event Envelope Schema
**File:** `src/PMS.Contracts/Events/EventEnvelope.cs`

Standard wrapper for all Kafka events:
```csharp
public class EventEnvelope
{
	public string EventId { get; set; }        // UUID for idempotency
	public string EventType { get; set; }      // "PatientCreated"
	public string Version { get; set; }        // "v1" for schema evolution
	public DateTime OccurredAt { get; set; }   // UTC timestamp
	public object Payload { get; set; }        // Actual event data
	public string? CorrelationId { get; set; } // Tracing
	public string Source { get; set; }         // "PatientService"
	public Dictionary<string, string>? Metadata { get; set; } // Retry count, etc.
}
```

**Impact:** Every event now has a unique `EventId` for deduplication.

---

### Step 2: Kafka Producer Hardening
**File:** `src/PMS.Common/Kafka/KafkaProducer.cs`

Producer configuration:
```csharp
Acks = Acks.All                    // Wait for all replicas
EnableIdempotence = true           // Exactly-once semantics
MessageSendMaxRetries = 10         // Retry transient failures
CompressionType = Snappy           // Network efficiency
```

**Graceful shutdown:**
```csharp
app.Lifetime.ApplicationStopping.Register(() =>
{
	kafkaProducer.Flush(TimeSpan.FromSeconds(10));
});
```

**Impact:** No events lost during shutdown or broker leader election.

---

### Step 3: Manual Offset Commit
**File:** `src/PMS.Common/Kafka/KafkaConsumerBase.cs`

Consumer configuration:
```csharp
EnableAutoCommit = false           // Manual control
AutoOffsetReset = Earliest         // Start from beginning if no offset
IsolationLevel = ReadCommitted     // Only see committed producer messages
```

**Processing flow:**
```csharp
var message = _consumer.Consume(cancellationToken);
var success = await ProcessMessageAsync(message);

if (success)
{
	_consumer.Commit(message);  // ← Only commit on success
}
// If fails, message will be re-delivered on restart
```

**Impact:** Consumer crashes don't lose messages. At-least-once delivery guaranteed.

---

### Step 4: Retry Topics + Dead Letter Queue
**Files:**
- `src/PMS.Common/Kafka/KafkaConsumerBase.cs` (retry logic)
- `src/PMS.AI/Services/PatientEventsRetryConsumer.cs`

**Flow:**
1. Main consumer tries processing
2. Failure → Send to `{topic}-retry` with `retryCount=1`
3. Retry consumer waits 2^retryCount seconds (exponential backoff)
4. Retry processing
5. Still fails → Increment retry count, send back to retry topic
6. After 3 attempts → Send to `{topic}-dlq` for manual investigation

**Retry delays:**
- Attempt 1: 2 seconds
- Attempt 2: 4 seconds  
- Attempt 3: 8 seconds
- After 3: Dead Letter Queue

**Impact:** 
- Transient failures auto-recover
- Poison messages don't block queue
- Ops team alerted to DLQ messages

---

### Step 5: Polly Resilience Policies
**Files:**
- `src/PMS.Common/Resilience/ResiliencePolicies.cs`
- `src/PMS.Patient/Services/BillingGrpcClient.cs` (enhanced)
- `src/PMS.AI/Services/LLMService.cs` (enhanced)

**Pipeline order:** Timeout → Retry → Circuit Breaker

**gRPC configuration (Patient → Billing):**
```csharp
Timeout: 5 seconds per attempt
Retry: 3 attempts, exponential backoff (1s, 2s, 4s)
Circuit Breaker: Opens after 50% failure rate (min 5 requests)
Break Duration: 30 seconds
```

**HTTP configuration (AI → LLM):**
```csharp
Timeout: 30 seconds (LLMs are slow)
Retry: Same as gRPC
Circuit Breaker: Same as gRPC
```

**Impact:**
- Network glitches don't fail user requests
- Failing services get breathing room to recover
- No cascading failures across service boundaries

---

## 🏗️ Architecture Patterns Applied

### 1. Transactional Outbox (Phase 2 foundation)
```
PatientService creates patient in DB transaction:
1. INSERT INTO Patient VALUES (...)
2. INSERT INTO OutboxMessages VALUES (event envelope)
3. COMMIT

OutboxPublisher background worker:
1. SELECT * FROM OutboxMessages WHERE IsPublished = false
2. Publish to Kafka
3. UPDATE OutboxMessages SET IsPublished = true
```

**Guarantee:** Events published iff entity changes committed.

---

### 2. Event Envelope Pattern
Every event wrapped in standard metadata:
- Enables idempotency via `EventId`
- Enables schema evolution via `Version`
- Enables distributed tracing via `CorrelationId`
- Enables retry tracking via `Metadata["RetryCount"]`

---

### 3. Consumer Idempotency
```csharp
var cacheKey = $"processed_event:{envelope.EventId}";
var alreadyProcessed = await redis.GetAsync(cacheKey);

if (alreadyProcessed != null)
{
	return true; // Skip, already processed
}

// Process event...

await redis.SetAsync(cacheKey, "processed", expiry: 7 days);
```

**Guarantee:** Replaying Kafka topic doesn't duplicate side effects.

---

### 4. Retry Topic Pattern
Separate consumer group for retries with exponential backoff:
- Main consumer: Fast path, immediate processing
- Retry consumer: Slow path, delayed processing

**Benefit:** Main consumer not blocked by retry delays.

---

### 5. Dead Letter Queue Pattern
After max retries, send to DLQ topic:
```json
{
  "OriginalMessage": "{full event envelope}",
  "Reason": "Max retries exceeded: 3",
  "Topic": "patient-created",
  "FailedAt": "2024-06-15T10:30:00Z",
  "ConsumerGroup": "ai-service-group"
}
```

**Benefit:** 
- Poison messages don't block healthy traffic
- Full audit trail for debugging
- Can replay manually after fixing root cause

---

### 6. Polly Resilience Pipeline
Defense in depth for external calls:

**Layer 1: Timeout**
- Prevents hanging indefinitely
- Protects thread pool

**Layer 2: Retry**
- Handles transient network glitches
- Exponential backoff prevents thundering herd
- Jitter adds randomness

**Layer 3: Circuit Breaker**
- Detects systemic failures across requests
- Fails fast when downstream is unhealthy
- Gives failing service breathing room
- Half-open state tests recovery

**Example flow:**
```
Request 1-5: All timeout after retries → Circuit OPENS
Request 6-100: Fail in 10ms (circuit open, no retries)
[30 seconds pass]
Request 101: Test request (half-open) → Success → Circuit CLOSES
Request 102+: Normal operation resumes
```

---

## 🎓 Key Learnings

### 1. Manual Offset Commit is Critical
**Mistake:** Auto-commit commits offsets every 5 seconds, regardless of processing success.

**Impact:** Consumer crash = lost messages.

**Solution:** `EnableAutoCommit=false`, commit only after successful processing.

**Tradeoff:** Duplicate processing on crash (mitigated by EventId deduplication).

---

### 2. Retry at Multiple Levels
We implemented retry at THREE levels:

| Level | Component | Purpose |
|-------|-----------|---------|
| 1 | Kafka Producer | Transient broker failures |
| 2 | Polly (gRPC/HTTP) | Network glitches |
| 3 | Kafka Consumer (Retry Topic) | Business logic failures |

**Why all three?**
- Producer retries: Milliseconds (network-level)
- Polly retries: Seconds (call-level)
- Consumer retries: Minutes (message-level)

Different failure domains need different retry strategies!

---

### 3. Circuit Breaker Prevents Cascades
**Scenario:** Billing service database crashes.

**Without circuit breaker:**
- 100 users × 27 seconds timeout = 2,700 seconds wasted
- 100 users × 4 attempts = 400 requests hammer dying service

**With circuit breaker:**
- First 5 users × 27 seconds = 135 seconds
- Next 95 users × 0.01 seconds = 0.95 seconds
- Only 20 requests hit Billing service
- Service gets 30 seconds breathing room every cycle

**Key insight:** Circuit breaker rejects requests **at the client side** before they hit the network!

---

### 4. Main Consumer vs Retry Consumer
**Initially confused:** Why two consumers?

**Clarity:** Separation of concerns!
- **Main consumer:** Fast path, fail fast, move on to next message
- **Retry consumer:** Slow path, exponential delays, doesn't block main queue

**Shared policy:** Both enforce `maxRetryAttempts=3` by checking `Metadata["RetryCount"]`.

---

### 5. DRY with Polly Policies
**Initial implementation:** Duplicated timeout/retry/circuit breaker config in `GetCombinedPolicy`.

**Refactor:** Compose individual policies:
```csharp
public static ResiliencePipeline<T> GetCombinedPolicy<T>(...)
{
	var timeoutPipeline = GetTimeoutPolicy<T>(...);
	var retryPipeline = GetRetryPolicy<T>(...);
	var circuitBreakerPipeline = GetCircuitBreakerPolicy<T>(...);

	return new ResiliencePipelineBuilder<T>()
		.AddPipeline(timeoutPipeline)
		.AddPipeline(retryPipeline)
		.AddPipeline(circuitBreakerPipeline)
		.Build();
}
```

**Benefit:** Single source of truth for each policy.

---

## 📊 Reliability Metrics

### Producer Guarantees
| Metric | Value | Meaning |
|--------|-------|---------|
| Acks | All | Wait for all in-sync replicas |
| Idempotence | Enabled | Exactly-once per partition |
| Retries | 10 | Auto-retry transient failures |
| Compression | Snappy | Reduce network bandwidth |

### Consumer Guarantees
| Metric | Value | Meaning |
|--------|-------|---------|
| Commit Mode | Manual | Only after processing |
| Offset Reset | Earliest | No message loss on first start |
| Isolation | ReadCommitted | Only see committed messages |
| Idempotency | EventId | Dedupe via Redis cache |

### Resilience Policies
| Component | Timeout | Retries | Circuit Breaker |
|-----------|---------|---------|-----------------|
| gRPC (Billing) | 5s | 3 (1s, 2s, 4s) | 50% @ 5 req, 30s break |
| HTTP (LLM) | 30s | 3 (1s, 2s, 4s) | 50% @ 5 req, 30s break |

---

## 🐛 Bugs Fixed During Phase

### Bug 1: Retry Consumer Had `maxRetryAttempts=0`
**Issue:** Would send to DLQ after just one retry.

**Root cause:** Misunderstood that both consumers check the same `Metadata["RetryCount"]`.

**Fix:** Both consumers now have `maxRetryAttempts=3`.

### Bug 2: Missing `IConfiguration` Using Statement
**Issue:** Build error in `KafkaConsumerBase.cs`.

**Fix:** Added `using Microsoft.Extensions.Configuration;`.

### Bug 3: Duplicate Polly Policy Code
**Issue:** `GetCombinedPolicy` duplicated logic from individual policy methods.

**Fix:** Refactored to compose individual policies via `AddPipeline`.

---

## 🔮 Future Improvements

### 1. Saga Pattern for Distributed Transactions
Current: Patient creation + Billing creation is not atomic across services.

**Risk:** Patient created but Billing fails → Orphaned patient.

**Solution:** Implement Saga orchestrator or choreography with compensating transactions.

---

### 2. Kafka Schema Registry
Current: Events serialized as JSON strings.

**Risk:** Breaking schema changes, no compatibility checking.

**Solution:** 
- Confluent Schema Registry (Avro)
- Enforce backward/forward compatibility
- Version evolution

---

### 3. Consumer Lag Monitoring
Current: No visibility into Kafka consumer lag.

**Risk:** Consumer falling behind, increased processing latency.

**Solution:**
- Prometheus `kafka_consumer_lag` metric
- Alert when lag > 1000 messages

---

### 4. Idempotency Key in Database
Current: EventId deduplication via Redis (7-day TTL).

**Risk:** Redis cache eviction = potential duplicates.

**Solution:**
- `ProcessedEvents` table in database
- Unique constraint on `EventId`
- Permanent record, no TTL

---

### 5. DLQ Replay Tooling
Current: Manual investigation of DLQ messages.

**Solution:**
- Admin CLI: `pf-admin dlq list --service=ai`
- Replay: `pf-admin dlq replay --event-id=abc-123`
- Bulk replay after fixing root cause

---

## ✅ Phase Success Criteria (All Met)

- [x] Events survive Kafka broker restart
- [x] Consumer crash doesn't lose messages
- [x] Replaying topic doesn't create duplicates
- [x] Transient gRPC failures auto-recover
- [x] Poison messages go to DLQ after 3 attempts
- [x] Circuit breaker opens when downstream fails
- [x] Solution builds with 0 warnings, 0 errors
- [x] All 5 commits pushed to branch
- [x] Documentation updated (ROADMAP.md)

---

## 📈 Impact Summary

**Before Phase 3:**
- ❌ Events lost during Kafka restarts
- ❌ Consumer crashes = data loss
- ❌ No retry logic for failures
- ❌ gRPC failures crash requests
- ❌ No circuit breaker protection

**After Phase 3:**
- ✅ Events survive broker failures (Acks=All, idempotence)
- ✅ At-least-once delivery guaranteed (manual offset commit)
- ✅ Automatic retry with exponential backoff
- ✅ Poison messages isolated in DLQ
- ✅ Circuit breaker prevents cascading failures
- ✅ Polly resilience on all external calls

**Reliability score:** From ~70% → ~99.9% (estimated, needs load testing to verify)

---

## 🚀 Next Phase Preview

**Phase 4: Security Hardening**

Focus areas:
- Refresh token flow
- JWT key rotation
- Account lockout
- Rate limiting
- Security headers
- Audit logging
- Non-root containers

**Goal:** Close OWASP Top 10 vulnerabilities, meet HIPAA §164.312(b) audit requirements.

---

## 🙏 Acknowledgments

Key resources that helped:
- Chris Richardson's Microservices Patterns (Transactional Outbox)
- Confluent Kafka best practices guide
- Polly v8 resilience pipelines documentation
- Microsoft's eShopOnContainers reference architecture

---

**Phase 3 Complete!** 🎉

Branch merged to `main` and tagged as `v0.4-phase-3`.
