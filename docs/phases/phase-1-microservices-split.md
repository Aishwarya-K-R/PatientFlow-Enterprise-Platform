# Phase 1 — Real Microservices Split

**Status:** ✅ Complete  
**Tag:** `v0.2-phase-1`  
**Duration:** 1 session  
**Branch:** `phase-1-microservices`

---

## 🎯 Goal

Convert the single `PMS.csproj` monolith into a proper multi-project .NET solution. Each service becomes its own project with its own image, `Program.cs`, and `DbContext`. Remove the `if (serviceName == "X")` switching pattern.

---

## ✅ What Was Delivered

### **Solution Structure**
```
PatientFlow.sln
├── src/
│   ├── PMS.Contracts/      ✅ Shared DTOs, protos, config models
│   ├── PMS.Common/         ✅ Exception handlers, Kafka producer
│   ├── PMS.Gateway/        ✅ YARP reverse proxy, Kafka topic creator
│   ├── PMS.Auth/           ✅ Own DbContext, AuthController, JWT
│   ├── PMS.Patient/        ✅ Own DbContext, IPatientRepository, gRPC client
│   ├── PMS.Billing/        ✅ Own DbContext, gRPC server
│   └── PMS.AI/             ✅ LLM + RAG + Redis
└── tests/
	├── PMS.Auth.Tests/     ✅ xUnit project
	├── PMS.Patient.Tests/  ✅ xUnit project
	├── PMS.Billing.Tests/  ✅ xUnit project
	└── PMS.AI.Tests/       ✅ xUnit project
```

### **Per-Service Changes**

#### **PMS.Contracts (Shared)**
- ✅ `AuthDtos.cs` - LoginResponse, SignupRequest, LoginRequest
- ✅ `AiDtos.cs` - AskRequest
- ✅ `AISettings.cs` - AI configuration model
- ✅ Proto files: `Billing_Service.proto`, `Patient_Event.proto`
- ✅ gRPC code generation configured

#### **PMS.Common (Shared Infrastructure)**
- ✅ `GlobalExceptionHandler` - Centralized error handling
- ✅ `DomainExceptions` - PatientNotFoundException, DuplicateEmailException
- ✅ `KafkaProducer` - Event publishing (reusable across services)
- ✅ Dependencies: Serilog, Prometheus, Kafka, Health Checks

#### **PMS.Gateway**
- ✅ YARP reverse proxy configuration
- ✅ Routes: `/auth/**`, `/api/**`, `/ai/**`
- ✅ Kafka topic creator (runs on startup)
- ✅ Health checks and metrics
- ✅ No business logic - pure routing

#### **PMS.Auth**
- ✅ `AuthDbContext` - Separate database for auth
- ✅ `User` model with `UserRole` enum
- ✅ `AuthService` - JWT generation, signup, login
- ✅ `AuthController` - Rate-limited login endpoint
- ✅ `RateLimiterConfig` - 5 attempts per minute
- ✅ EF Core migrations
- ✅ Builds independently

#### **PMS.Patient**
- ✅ `PatientDbContext` - Separate database for patients
- ✅ `Patient` model
- ✅ `IPatientRepository` + `PatientRepository` - Repository pattern
- ✅ `PatientService` - Business logic with 2-tier caching (Memory + Redis)
- ✅ `PatientController` - CRUD endpoints with authorization
- ✅ `BillingGrpcClient` - Calls Billing service via gRPC
- ✅ Publishes `PatientCreated/Updated/Deleted` events to Kafka
- ✅ **Synchronous gRPC + async Kafka** for reliability
- ✅ Builds independently

#### **PMS.Billing**
- ✅ `BillingDbContext` - Separate database for billing
- ✅ `BillingAccount` model
- ✅ `BillingAccountService` - Creates billing accounts
- ✅ `BillingGrpcService` - gRPC server implementation
- ✅ Publishes `BillingCreated` events to Kafka
- ✅ **Removed self-loop** - No longer calls itself via gRPC
- ✅ Builds independently

#### **PMS.AI**
- ✅ `AIController` - Admin-only AI queries
- ✅ `LLMService` - Ollama integration
- ✅ `RedisService` - Patient context caching
- ✅ Configured with system prompt and rules
- ✅ JWT authentication
- ✅ Builds independently

### **Configuration & Deployment**
- ✅ Per-service `appsettings.json` with proper defaults
- ✅ Per-service `Dockerfile` with multi-stage builds
- ✅ `docker-compose.microservices.yml` with all services
- ✅ Postgres init script for multiple databases
- ✅ NuGet.Config (uses nuget.org only)

### **Namespace Cleanup**
- ✅ Old: `Patient_Management_System.*`
- ✅ New: `PatientFlow.{Service}.*`
- ✅ Consistent naming across all projects

---

## 🏗️ Architecture Decisions

### **1. gRPC for Synchronous Communication**
**Decision:** Patient service calls Billing service via gRPC when creating a patient.

**Rationale:**
- Immediate feedback (synchronous)
- Strong typing via protobuf
- HTTP/2 performance
- Better than REST for inter-service RPC

**Trade-off:** Single point of failure if Billing is down (mitigated by Kafka retry in Phase 3)

---

### **2. Kafka for Event Notification**
**Decision:** Each service publishes its own domain events.

**Flow:**
```
Patient → Publishes PatientCreated
Billing → Publishes BillingCreated
AI → Subscribes to both
```

**Rationale:**
- Each service owns its events (proper domain boundaries)
- AI can build context from multiple sources
- Future services can subscribe without changing producers
- Audit trail

**Rejected Alternative:** Patient publishes `PatientCreated` to trigger Billing  
**Why:** That would make Billing's business logic dependent on Patient's events. Instead, Patient makes an explicit request (gRPC).

---

### **3. Removed Billing Kafka Consumer**
**Decision:** Billing does NOT listen to `PatientCreated` events.

**Before (Monolith):**
```
Patient → Kafka → Billing Consumer → gRPC to self ❌
```

**After (Microservices):**
```
Patient → gRPC → Billing ✅
Billing → Kafka → BillingCreated (notification)
```

**Rationale:**
- Eliminated gRPC self-loop (network overhead for local call)
- Cleaner architecture (sync for requests, async for notifications)
- No duplicate account creation issues

---

### **4. Simplified Idempotency (Phase 1 Scope)**
**Decision:** Removed idempotency checks and unique DB index.

**Rationale:**
- Phase 1 goal: get microservices working
- Single gRPC path reduces duplicate risk
- Can add proper idempotency in Phase 3 (Event-driven reliability)

**Risk:** If Patient service retries gRPC, might create duplicate billing accounts  
**Mitigation:** Will add Polly retry + idempotency in Phase 3

---

### **5. Per-Service Database**
**Decision:** Each service has its own database.

**Databases:**
- `patientflow_auth` - Users
- `patientflow_patient` - Patients
- `patientflow_billing` - Billing accounts

**Rationale:**
- True microservices principle (data ownership)
- Can scale databases independently
- No shared schema coupling
- Clear service boundaries

---

### **6. Shared Libraries (Contracts + Common)**
**Decision:** Two shared class libraries for cross-cutting concerns.

**PMS.Contracts:**
- DTOs (data transfer)
- Proto files (gRPC contracts)
- Configuration models

**PMS.Common:**
- Exception handlers
- Kafka producer
- Cross-cutting infrastructure

**Rationale:**
- Avoid code duplication
- Single source of truth for contracts
- Centralized infrastructure code

**Trade-off:** Creates a dependency between services (but only on stable contracts)

---

## 🐛 Issues Encountered & Resolved

### **1. NuGet Source Conflict**
**Problem:** Corporate Lytx NuGet sources in global config caused restore failures.

**Solution:**
- Created `NuGet.Config` at repo root
- Uses only `nuget.org`
- Overrides global config

**Learning:** Always control dependency sources in the repo.

---

### **2. gRPC Self-Loop**
**Problem:** Old monolith had Billing's Kafka consumer calling Billing via gRPC (network call to itself).

**Solution:**
- Removed `BillingKafkaConsumer`
- Patient service calls Billing directly via gRPC
- Billing consumer no longer needed

**Learning:** Watch for inefficient patterns when splitting monoliths.

---

### **3. Potential Duplicate Billing Accounts**
**Problem:** Both gRPC and Kafka paths could create accounts.

**Initial Solution:** Added idempotency check + unique index  
**Final Solution:** Removed Kafka consumer, single gRPC path

**Learning:** Simplify architecture before adding complexity.

---

### **4. Proto File Compilation**
**Problem:** gRPC code generation needed `Grpc.AspNetCore` package.

**Solution:**
- Added to `PMS.Contracts.csproj`
- All services now reference Contracts for proto access

**Learning:** gRPC tooling requires runtime packages, not just Tools.

---

## 📊 Metrics

| Metric | Value |
|--------|-------|
| **Projects** | 11 (7 services + 4 tests) |
| **Lines of Code Moved** | ~2,000+ |
| **Namespaces Renamed** | All `Patient_Management_System.*` → `PatientFlow.*` |
| **Dockerfiles Created** | 5 |
| **Build Time** | ~25 seconds (full solution) |
| **Warnings** | 0 |
| **Errors** | 0 |

---

## ✅ Done When Criteria (Met)

- [x] Each service builds independently
- [x] `docker compose up patient-service` brings up only Patient + its deps
- [x] Each test project runs independently
- [x] No more `if (serviceName == "X")` in Program.cs
- [x] Per-service Dockerfile
- [x] Namespace cleanup complete
- [x] gRPC self-loop removed

---

## 🚀 What's Next (Phase 2)

### **Data Ownership & Validation**
- Per-service Postgres schema (`auth`, `patient`, `billing`)
- FluentValidation for request DTOs
- Unique indexes on `Patient.Email`, `User.Email`
- **Outbox table** pattern for Phase 3 reliability
- File-based SQL migrations

---

## 💡 Key Learnings

### **1. Microservices != Just Splitting Code**
Splitting code is easy. The hard parts are:
- Event ownership (who publishes what?)
- Synchronous vs async communication patterns
- Avoiding chatty inter-service calls
- Managing distributed transactions

### **2. Start Simple, Add Complexity Later**
We removed idempotency and Kafka consumer to simplify Phase 1. Better to get the basics working than over-engineer upfront.

### **3. gRPC for Request/Response, Kafka for Events**
Clear separation:
- **gRPC** - When you need a response (Patient → Billing)
- **Kafka** - When you notify others (Patient → PatientCreated)

### **4. Shared Libraries Are Okay**
Purists say "no shared code." Reality: Contracts and infrastructure are fine to share. Just don't share business logic.

### **5. Docker Compose for Local Dev**
Docker Compose is perfect for local microservices development. Kubernetes can wait for Phase 8.

---

## 🎓 Code Quality Improvements

### **Before (Monolith)**
```csharp
// Single Program.cs with conditional logic
if (serviceName == "Billing")
{
	builder.Services.AddHostedService<KafkaConsumer>();
}

// Kafka consumer calling itself via gRPC
await _billingGrpcClient.CreateBillingAccountAsync(patientId);
```

### **After (Microservices)**
```csharp
// PMS.Billing/Program.cs - Clean, focused
builder.Services.AddScoped<BillingAccountService>();
builder.Services.AddGrpc();
app.MapGrpcService<BillingGrpcService>();

// PMS.Patient calls Billing directly
await _billingGrpcClient.CreateBillingAccountAsync(newPatient.Id);
```

---

## 🎯 Success Criteria

✅ **All services build independently**  
✅ **No shared database**  
✅ **Clear service boundaries**  
✅ **gRPC for sync, Kafka for async**  
✅ **No circular dependencies**  
✅ **Docker Compose ready**  
✅ **Portfolio-ready architecture**  

---

## 📝 Deferred to Later Phases

- [ ] Retry logic (Phase 3)
- [ ] Idempotency at scale (Phase 3)
- [ ] FluentValidation (Phase 2)
- [ ] EF Migrations per service (Phase 2)
- [ ] Kubernetes deployment (Phase 8)
- [ ] Integration tests (Phase 7)
- [ ] Polly resilience (Phase 3)

---

**Phase 1 complete! Ready for Phase 2: Data Ownership & Validation.** 🎉
