# Phase 2 — Data Ownership & Validation

**Status:** ✅ Complete
**Tag:** `v0.3-phase-2`
**Branch:** `phase-2-data-ownership`
**Sessions:** 2

---

## 🎯 Goal

Establish proper data ownership boundaries between services. Each microservice
fully owns its data, request validation moves to a structured framework, the
Outbox pattern is introduced as the foundation for Phase 3 reliability, and
schema changes are now driven by versioned migration files instead of being
implicit.

---

## ✅ What Was Delivered

### **1. FluentValidation across services**

Replaced inline `if (string.IsNullOrWhiteSpace(...))` validation guards with
strongly-typed validators registered in DI:

- `SignupRequestValidator` — email format, password complexity (min length,
  uppercase, lowercase, digit, special char)
- `LoginRequestValidator` — email + password presence
- `AskRequestValidator` — question length 1..1000
- `PatientValidator` — name + address presence and length, DOB in the past,
  registered date >= DOB

Validators are picked up automatically via
`AddValidatorsFromAssemblyContaining<T>()` and registered with the ASP.NET
Core model-binding pipeline via `AddFluentValidationAutoValidation()`.
Controllers no longer need `if (!ModelState.IsValid) return BadRequest(...)`
boilerplate; the pipeline returns a 400 with a structured `ValidationProblemDetails`
response automatically.

### **2. Unique indexes on Email columns**

Added unique indexes via Fluent API in each DbContext's `OnModelCreating`:

- `IX_Users_Email_Unique` on `AuthDbContext.Users.Email`
- `IX_Patients_Email_Unique` on `PatientDbContext.Patients.Email`

Database-level uniqueness guarantees correctness even under race conditions
between two concurrent inserts. The service-level "check then insert" pattern
is preserved for friendly UX, but the index is the source of truth.

`DbUpdateException` is caught in `AuthService.Signup` and in
`PatientRepository.AddAsync`/`UpdateAsync` — the constraint name
(`IX_Users_Email_Unique` / `IX_Patients_Email_Unique`) is matched in the
inner exception message and translated to a `DuplicateEmailException`.

### **3. Outbox table per service**

The transactional outbox pattern is implemented in **Patient** and **Billing**:

- `OutboxMessage` entity with fields: `Id`, `Topic`, `Payload`,
  `CreatedAt`, `PublishedAt`, `IsPublished`, `RetryCount`, `ErrorMessage`
- Composite index `(IsPublished, CreatedAt)` for efficient "find unpublished,
  oldest first" queries
- Entity write + outbox row are committed in the **same database transaction**
  (atomicity guaranteed by EF's `SaveChangesAsync`, plus an explicit
  transaction for Patient Create — see below)
- `OutboxPublisherService` background worker polls every 10 seconds, publishes
  unpublished rows to Kafka via `KafkaProducer.PublishRawAsync` (avoids
  double-serialization), marks them published or increments `RetryCount`
- Maximum 5 retries before a message is left in a `Failed`-like state
  (still in the table, but no longer attempted)

### **4. Outbox cleanup (new addition)**

`OutboxPublisherService` only marks rows as published; it doesn't remove
them. To prevent the outbox table from growing unbounded, a separate
`OutboxCleanupService` background worker was added per service:

- Runs once per hour
- Deletes rows where `IsPublished = true` AND `PublishedAt < NOW() - 7 days`
- Uses EF Core's `ExecuteDeleteAsync` for a single server-side `DELETE` (no
  rows pulled into memory)
- Registered as a hosted service alongside `OutboxPublisherService` in both
  Patient and Billing

### **5. File-based EF migrations**

Each service now has versioned migration files in its own `Migrations/`
folder:

- `src/PMS.Auth/Migrations/InitialCreate` — creates `Users` table + unique
  email index
- `src/PMS.Patient/Migrations/InitialCreate` — creates `Patients` table,
  `OutboxMessages` table, unique email index, composite outbox index
- `src/PMS.Billing/Migrations/InitialCreate` — creates `BillingAccounts`
  table + `OutboxMessages` table + composite outbox index

`Migrate()` is called on startup, which applies any new migrations against
the database. In Phase 8 this will move to a dedicated Kubernetes Job so
multi-replica deployments don't race.

### **6. Saga transaction for Patient Create**

A subtle but important fix to the initial Step-3 implementation: the outbox
event for `PatientCreated` was being constructed BEFORE the patient was
saved, so `newPatient.Id` was always `0` in the payload. Downstream
consumers (AI service for RAG) received `PatientId=0` for every create.

Fix: `CreatePatientAsync` now opens an explicit transaction. The patient
is inserted first so EF populates the Id from the database, then the gRPC
call to Billing happens (inside the transaction — if it fails, everything
rolls back), then the outbox event is inserted with the real Id, and only
then the transaction commits.

Update and Delete don't need explicit transactions because the Id is known
from the URL parameter; a single `SaveChangesAsync` is already atomic for
the entity write + outbox row.

---

## 🏗️ Architectural Decisions

### **1. Per-database (not per-schema) data ownership**

The original Phase 2 plan called for per-service **schemas** within a single
Postgres database (`auth`, `patient`, `billing` schemas under one database).
The actual implementation went further: **each service has its own database**
(`patientflow_auth`, `patientflow_patient`, `patientflow_billing`).

**Why this is an upgrade over the plan:**
- True data sovereignty — no cross-schema queries possible by accident
- A misconfigured query in Auth can't reach Patient's data even with bad
  Postgres privileges
- Easier to evolve to physically separate database hosts later (just change
  the connection string — no schema migration)
- Backup/restore boundaries match service boundaries

**Why this is OK now:**
- All three databases still live in the same Postgres instance for local
  dev (Phase 8 may split them across multiple instances)
- Migration history table (`__EFMigrationsHistory`) is per-database, so
  each service's migrations are tracked independently

### **2. `db.Database.Migrate()` on startup (interim)**

For now, each service applies its migrations on startup via
`db.Database.Migrate()`. This is **acceptable for local development with
one replica per service** but is a known anti-pattern in production with
multi-replica deployments (race condition between starting pods).

**Deferred to Phase 8:** Move migrations to a pre-deploy Kubernetes Job.
The Job runs `dotnet ef database update` against the target DB, completes,
then the app pods are allowed to start. This pattern is documented in
the roadmap and the existing migrations directly support it.

### **3. Saga rollback over async retry for Patient → Billing**

The initial Phase 1 design called for synchronous gRPC from Patient to
Billing. The bug found in Phase 2 was that a gRPC failure was silently
swallowed — patient + outbox written, billing never created, caller saw
HTTP 200.

The fix wraps the gRPC call **inside** the patient-creation transaction.
A gRPC failure now rolls back the patient and outbox — the caller sees
the real error and can retry the whole operation. This restores the
"strong consistency" guarantee the gRPC choice was supposed to provide.

Trade-off: the database transaction is held open across the gRPC call. If
Billing is slow, the patient row's lock is held longer. Acceptable for
local-cluster scale (~50ms gRPC). For real production, would consider
either a saga orchestrator OR moving back to async Kafka publishing for
billing.

### **4. Outbox JSON payload sent as-is**

The outbox publisher initially called
`JsonSerializer.Deserialize<object>(message.Payload)` and then passed the
result to `KafkaProducer.PublishAsync(object)` which re-serialized it.
This double round-trip is wasteful and fragile (relies on `JsonElement`
round-tripping correctly).

Fix: added a `PublishRawAsync(topic, jsonString)` overload to
`KafkaProducer` and changed both outbox publishers to use it. The string
stored in the database is already valid JSON; we just send it directly.

---

## 🐛 Bugs Encountered & Resolved

### **1. PatientId=0 silent data corruption**

Already documented above. Root cause was constructing the outbox payload
before `SaveChangesAsync` populated the auto-generated Id. Fixed with
explicit transaction + save-flush-call-save-commit ordering.

### **2. gRPC failure swallowed silently**

Already documented above. Fixed by bringing the gRPC call inside the
transaction so its failure can trigger rollback.

### **3. Double JSON serialization in outbox publisher**

Already documented above. Fixed with `PublishRawAsync` overload.

### **4. Missing outbox cleanup**

Not strictly a bug — but a missing piece. Published rows accumulated
indefinitely. Added `OutboxCleanupService` per service with a 7-day
retention window.

---

## ✅ Done When Criteria

| Criterion | Status |
|---|---|
| FluentValidation registered and active in Auth, Patient, AI | ✅ |
| Unique indexes on Email columns enforced at the DB | ✅ |
| Outbox table + publisher worker in Patient and Billing | ✅ |
| Outbox cleanup worker in Patient and Billing | ✅ |
| Per-service data ownership (database boundary) | ✅ (better than plan) |
| File-based EF migrations per service | ✅ |
| Build clean: `dotnet build PatientFlow.sln` → 0 errors, 0 warnings | ✅ |
| Retrospective documented | ✅ (this file) |
| Phase 2 merged into main, tagged `v0.3-phase-2` | (pending merge) |

---

## 📌 Deferred to Later Phases

| Item | Phase |
|---|---|
| Migrations applied via K8s pre-deploy Job (instead of app startup) | Phase 8 |
| Row-level locking on outbox polling (`FOR UPDATE SKIP LOCKED`) for safe multi-replica scaling | Phase 8 |
| Postgres LISTEN/NOTIFY for instant outbox wakeup (replaces 10-sec polling) | Phase 6 |
| Inline `Validate()` method in PatientService duplicates FluentValidation — remove | Phase 7 cleanup |
| Audit log for PHI access | Phase 4 |
| Column-level encryption for PHI fields | Phase 5 |
| Soft delete + retention policy for patients (HIPAA) | Phase 12 |

---

## 💡 Key Learnings

### **1. EF Core's implicit transaction is enough most of the time**

`SaveChangesAsync` already wraps multiple `Add`/`Update`/`Remove` calls in
one database transaction. You only need explicit `BeginTransactionAsync`
when:
- You have **two or more `SaveChangesAsync` calls** in one logical operation
- One save depends on the **output of an earlier save** (e.g., auto-generated Id)
- An **external side-effect** (gRPC, HTTP) must succeed before commit

Update and Delete in PatientService don't need explicit transactions because
they do all writes in one `SaveChangesAsync` and the Id is known up front.

### **2. The Outbox pattern is just a table + a worker**

It feels fancy but it's literally:
- A table with `Payload`, `IsPublished`, `PublishedAt`, `RetryCount`
- A background worker that polls for `IsPublished=false` and publishes them

The hard part is getting the transactional boundary right — make sure the
outbox row is committed with the entity write, not after.

### **3. Per-database > per-schema for service isolation**

Both achieve "no cross-service writes by accident", but per-database also
gives:
- Different connection strings (visible separation in config)
- Different backup boundaries
- Easier physical separation later

For a learning project the cost is the same (still one Postgres container);
for a real production system you'd eventually move them to different DB
instances anyway.

### **4. `Migrate()` with no migrations is silent**

If you call `db.Database.Migrate()` and there are no migration files in your
project, EF Core does **nothing** (no error, no tables). This trips people up
who think `Migrate()` implies "create tables from model" — it doesn't. Use
`EnsureCreated()` for that, or generate proper migrations. We went with
the latter.

---

## 🚀 What's Next (Phase 3)

**Event-driven reliability.** Many of the outbox pieces we built here become
more sophisticated:

- Outbox relay gets stronger guarantees (idempotent producer, exactly-once
  semantics, `Acks=All`)
- Kafka consumers move to manual offset commit after success
- Retry topic + DLQ topic per consumer; exponential backoff
- Event envelope schema: `eventId`, `eventType`, `version`, `occurredAt`,
  `payload` — consumers dedupe by `eventId`
- Polly resilience pipelines (retry + circuit breaker + timeout) on every
  HTTP/gRPC call between services

The data foundations from Phase 2 (outbox tables, transactional boundaries,
proper migrations) make Phase 3 mostly an "upgrade the existing outbox/Kafka
flow with reliability guarantees" effort rather than a rewrite.
