# PatientFlow Enterprise Platform — Production-Readiness Roadmap

A 12-phase plan to evolve a learning prototype into a production-grade,
HIPAA-aware healthcare microservices platform. Each phase lands on `main`
via a single PR and is tagged at completion.

---

## Scoping decisions

| Decision | Value |
|---|---|
| Deploy target | Local — Docker Compose + Minikube/kind (no cloud bill) |
| Compliance bar | Full HIPAA simulation — audit log, column-level PHI encryption, threat model, retention policy, control mapping |
| Runtime | .NET 8 LTS |
| Repo | https://github.com/Aishwarya-K-R/PatientFlow-Enterprise-Platform |

The local target means we use **operators and self-hosted equivalents** of
what cloud providers would offer: cert-manager for TLS, Sealed Secrets /
SOPS for secret management, CloudNativePG operator for Postgres, Strimzi
for Kafka, Ollama in-cluster for LLM. The result is a credible production
simulation without the cloud bill.

---

## At-a-glance summary

| # | Phase | Status | Tag | Sessions | Est. cost |
|---|---|---|---|---|---|
| 0 | Foundation cleanup | ✅ Done | `v0.1-phase-0` | 1 | ~$30 |
| 1 | Real microservices split | ✅ Done | `v0.2-phase-1` | 1 | – |
| 2 | Data ownership & validation | ✅ Done | `v0.3-phase-2` | 2 | $20–40 |
| 3 | Event-driven reliability | ✅ Done | `v0.4-phase-3` | 2 | $25–45 |
| 3.5 | Cache warming (CRITICAL) | ✅ Done | `v0.4.1-phase-3.5` | 0.5 | – |
| 4 | Security hardening | – | `v0.5-phase-4` | 2 | $25–45 |
| 5 | TLS & PHI protection | – | `v0.6-phase-5` | 1–2 | $20–40 |
| 6 | Observability | – | `v0.7-phase-6` | 2 | $30–50 |
| 7 | Real tests | – | `v0.8-phase-7` | 2 | $20–40 |
| 8 | Kubernetes hardening | – | `v0.9-phase-8` | 2 | $30–55 |
| 9 | CI/CD productionize | – | `v0.10-phase-9` | 1–2 | $20–40 |
| 10 | Proper RAG + safer LLM | – | `v0.11-phase-10` | 2 | $25–45 |
| 11 | MCP server integration | – | `v0.12-phase-11` | 1–2 | $20–35 |
| 12 | Compliance polish & docs | – | `v1.0` | 1 | $15–30 |
| | **Estimated total** | | | **~22.5** | **$280–525** |

---

## Phase 0 — Foundation Cleanup 

**Goal:** Make the broken code base buildable, testable, and credibly secure from a clean clone. Stop the bleeding before doing surgery.

**Delivered:**
- `.gitignore` + `.dockerignore`; untrack 1869 build/runtime artifacts
- Secrets out of git (`.env.example`, `Kubernetes/secrets.example.yml`)
- Fixed broken `PMS.Tests.csproj` ProjectReference
- Excluded `PMS.Tests/**` from `PMS.csproj` implicit glob
- Replaced broken `GetPatientsSP` SQL with safe LINQ + allow-list
- Extracted `IPatientRepository`; `PatientService` no longer touches `DbContext`
- `[Authorize(Roles=ADMIN)]` on `/ai/ask`; pseudonymized patient names sent to LLM
- Versioned Redis cache keys (cache invalidation pattern)
- JSON `LoginResponse`; async `Signup`; `AskRequest` DTO; UTC for JWT expiry
- Retrospective doc: `docs/phases/phase-0-foundation-cleanup.md`

**Tag:** `v0.1-phase-0` on commit `20b0032` (merge of phase-0-foundation → main)

---

## Phase 1 — Real microservices split

**Goal:** Convert the single `PMS.csproj` into a proper multi-project .NET solution. Each service becomes its own project with its own image, `Program.cs`, and `DbContext`. Remove the `if (serviceName == "X")` switching pattern.

**Target structure:**
```
PatientFlow.sln
├── src/
│   ├── PMS.Contracts/      ← shared DTOs, protos, event envelopes
│   ├── PMS.Common/         ← logging, exception handlers, health checks
│   ├── PMS.Gateway/        ← YARP reverse proxy only
│   ├── PMS.Auth/           ← own DbContext, AuthController
│   ├── PMS.Patient/        ← own DbContext, IPatientRepository
│   ├── PMS.Billing/        ← own DbContext, gRPC server, Kafka consumer
│   └── PMS.AI/             ← LLM + RAG + Kafka consumer
└── tests/
    ├── PMS.Auth.Tests/
    ├── PMS.Patient.Tests/
    ├── PMS.Billing.Tests/
    └── PMS.AI.Tests/
```

**Key changes:**
- `PatientFlow.sln` at repo root
- 5 service projects + 2 shared libraries
- Per-service `Program.cs` — no more service-routing switch
- Remove gRPC self-loop in Billing
- Per-service Dockerfile (only builds that project)
- Namespace cleanup: `Patient_Management_System.*` → `PatientFlow.{Service}.*`
- Sweep nullable warnings during the file moves

**Done when:** Each service builds independently; `docker compose up patient-service` brings up only Patient + its deps; each test project runs independently.

**Pre-read while waiting:**
- Microsoft `eShopOnContainers` reference repo
- `dotnet new sln`, `dotnet sln add`, `dotnet new classlib` commands
- "DbContext per microservice" Microsoft docs

---

## Phase 2 — Data ownership & validation

**Goal:** Each service owns its data. Introduce the Outbox table pattern as the foundation for Phase 3 reliability.

**Key changes:**
- Per-service Postgres schema (`auth`, `patient`, `billing`)
- FluentValidation for request DTOs
- Unique indexes on `Patient.Email`, `User.Email`
- **Outbox table** per service — events written transactionally with entity writes
- File-based SQL in EF migrations (not C# string literals)
- `dotnet ef --idempotent` script generated for prod use

**Done when:** Dropping Billing's data doesn't affect Patient; outbox rows are committed atomically with their corresponding entity rows.

**Pre-read:** Transactional Outbox pattern (Chris Richardson), FluentValidation docs.

---

## Phase 3 — Event-driven reliability ✅

**Goal:** Survive broker restarts, replays, partial failures. The outbox table from Phase 2 finally gets shipped to Kafka properly.

**Delivered:**
- **Event Envelope schema** — `EventId`, `EventType`, `Version`, `OccurredAt`, `Payload`, `Metadata`
- **Kafka producer hardening** — `Acks=All`, `EnableIdempotence=true`, `Flush` on shutdown
- **Manual offset commit** — `EnableAutoCommit=false`, commit only after successful processing
- **Retry topics + DLQ** — exponential backoff (2s, 4s, 8s), poison messages sent to DLQ after 3 attempts
- **Polly resilience policies** — timeout → retry → circuit breaker for gRPC and HTTP calls
- `KafkaConsumerBase` — reusable consumer with automatic retry and DLQ handling
- `PatientEventsConsumer` + `PatientEventsRetryConsumer` in AI service
- `ResiliencePolicies` helper — centralized Polly configuration

**Done when:** Killing Kafka mid-write doesn't lose events; replaying a topic doesn't create duplicate billing accounts; transient gRPC failures auto-retry.

**Tag:** `v0.4-phase-3` on commit 506a388 (merge of phase-3-event-reliability → main)

**Retrospective:** `docs/phases/phase-3-event-reliability-retrospective.md`

**Pre-read:** Kafka idempotent producers, consumer group rebalancing, Polly v8 resilience pipelines.

---

## Phase 3.5 — Cache Warming (CRITICAL Enhancement) ✅

**Goal:** Fix cold start problem where AI service only has data for events consumed after deployment.

**Delivered:**
- **PatientCacheWarmer** service — runs on AI service startup
- Fetches ALL existing patients from Patient Service via HTTP
- Loads full snapshot into Redis (one-time warm-up)
- Marks cache as initialized to skip on subsequent restarts
- Kafka consumers provide incremental updates to pre-warmed cache
- GET `/api/patients/all` endpoint in Patient Service (SYSTEM role only)
- `PatientSnapshotDto` — lightweight DTO for cache warming

**Architecture:** Hybrid approach (Cache Warming + Event-Driven Updates)
- **Startup:** Full snapshot from database → Redis (one-time)
- **Runtime:** Incremental updates via Kafka events (real-time)
- **Pattern:** Cache-Aside with Event Invalidation (Netflix, Facebook, Amazon)

**Problem solved:** Pure event-driven approach left AI service blind to historical patients created before consumer started. Users querying old patients got "no information available" despite data existing in database.

**Tag:** `v0.4.1-phase-3.5` on commit b7bca50 (merge of phase-3.5-cache-warming → main)

**Documentation:** `docs/phases/phase-3.5-cache-warming.md` (CRITICAL - explains problem, solution, trade-offs)

---

## Phase 4 — Security hardening

**Goal:** Close the obvious security holes. Audit-grade defenses.

**Key changes:**
- **Refresh token** flow; Redis-backed token revocation list
- JWT `kid` header; key rotation strategy
- **Account lockout** after N failed login attempts
- **Partitioned rate limiter** keyed on IP + email (not global)
- Password complexity rules
- Security headers middleware (HSTS, CSP, X-Frame-Options, etc.)
- Explicit CORS allow-list
- Strict error responses (no `exception.Message` leak)
- **Audit log table** — every PHI access logged
- Non-root container `USER`; read-only root FS

**Done when:** OWASP ZAP baseline scan returns no high-severity findings; brute-force test gets locked out.

**Pre-read:** OWASP Top 10, HIPAA §164.312(b) Audit Controls requirement.

---

## Phase 5 — TLS & PHI protection

**Goal:** Encryption everywhere — in transit and at rest.

**Key changes:**
- **cert-manager** + self-signed cluster issuer for ingress TLS
- Kafka **SASL_SSL**
- Postgres **SSL** required
- Redis **AUTH + TLS**
- Service-to-service: explicit TLS or service mesh (Linkerd)
- **Column-level encryption** for PHI: `Patient.Email`, `Patient.Address`, `Patient.Name`
- PHI minimization layer — only pseudo-IDs flow to LLM

**Done when:** `tcpdump` between pods shows no plaintext PHI; DB dump shows ciphertext for PHI columns.

**Pre-read:** TLS basics, cert-manager docs, EF Core data-protection providers.

---

## Phase 6 — Observability

**Goal:** Know what's happening in production. Traces, logs, metrics, alerts.

**Key changes:**
- **OpenTelemetry SDK** across all services with W3C TraceContext propagation
- OTLP exporter to **Tempo** (or Jaeger); Grafana wired to it
- **Serilog** enriched with `TraceId`/`SpanId`; logs shipped to **Loki**
- Stop writing logs to local files
- Grafana dashboards-as-code in `observability/grafana/dashboards/*.json`
- Prometheus alert rules (`alerts.yml`) + Alertmanager
- Business metrics: `patients_created_total`, `billing_failures_total`, `llm_request_latency_seconds`, cache hit ratio

**Done when:** You can click a slow gateway request in Grafana and follow the trace through Patient → Kafka → Billing.

**Pre-read:** OpenTelemetry concepts, three pillars of observability, structured logging templates.

---

## Phase 7 — Real tests

**Goal:** Tests that actually verify the system, runnable from a clean clone.

**Key changes:**
- **Testcontainers** (Postgres, Redis, Kafka) — per-test-class isolated infrastructure
- Custom `WebApplicationFactory` that seeds a known admin + test user
- Unit tests with **Moq** for every Service class
- Contract tests for proto messages + Kafka event schemas
- Coverage threshold via **coverlet** ≥ 70%, enforced in CI
- **k6** smoke load test in `loadtests/` (5 RPS for 1 min, no errors, p95 < 500ms)

**Done when:** CI runs all tests against real containers in under 5 minutes and they pass.

**Pre-read:** Testcontainers .NET docs, k6 documentation.

---

## Phase 8 — Kubernetes hardening

**Goal:** A cluster won't fall over when something goes wrong.

**Key changes:**
- **Helm chart** (or Kustomize overlays for `dev`/`staging`/`prod`)
- Each Deployment: `resources.requests/limits`, `liveness`/`readiness`/`startup` probes
- `securityContext`: runAsNonRoot, readOnlyRootFilesystem, drop ALL capabilities
- **HPA** on patient + gateway; **PDB** with `minAvailable: 1`
- `topologySpreadConstraints` across zones
- **NetworkPolicies**: deny-all egress by default, allow specific
- Per-service ServiceAccount; no use of `default`
- Postgres → **CloudNativePG** operator (1 primary + 2 replicas + backups)
- Kafka → **Strimzi** operator (3-broker KRaft cluster, persistent volumes)
- Ingress TLS with real hostname
- EF migrations as a pre-deploy **K8s Job** (no more `Migrate()` on app startup)

**Done when:** `kubectl delete pod patient-xxx` causes no user-visible impact; killing the Postgres primary fails over to a replica.

**Pre-read:** Minikube basics, Helm vs Kustomize, CloudNativePG / Strimzi operators.

---

## Phase 9 — CI/CD productionize

**Goal:** Every commit produces a reproducible, signed, scanned, tagged artifact and deploys automatically.

**Key changes:**
- Workflow rewrite: matrix per service, `dotnet test`, build, **Trivy** scan, SBOM (Syft), **Cosign** sign
- Tag images with `${{ github.sha }}` + semver on release; no more `:latest`
- **buildx** multi-arch (amd64 + arm64) + GHA layer cache
- **Dependabot** + **CodeQL**
- GitOps deploy via **ArgoCD** (or simpler: `kubectl apply` from a deploy job)

**Done when:** Opening a PR runs full test suite + image scan; merging to `main` produces a tagged signed image and deploys to `dev` cluster without manual steps.

**Pre-read:** GitHub Actions reusable workflows, Trivy + Cosign basics.

---

## Phase 10 — Proper RAG + safer LLM

**Goal:** Replace "stuff every patient into the prompt" with a real retrieval pipeline.

**Key changes:**
- **pgvector** extension on Postgres (or Qdrant as a separate service)
- Embedding job: on `PatientCreated`/`Updated` events, compute embeddings via a local model (BGE / nomic-embed) and store
- `AIController` does **top-K vector search** instead of dumping all patients
- Run **Ollama in-cluster** as a Deployment (proper service URL, not `host.docker.internal`)
- Prompt-injection defenses: input sanitization, structured output schema, optional Llama Guard
- Streaming responses (SSE) for better UX

**Done when:** AI answers stay accurate as patient count grows to 100k; no PHI leaves the trust boundary.

**Pre-read:** Vector search concepts, embeddings basics, pgvector docs.

---

## Phase 11 — MCP server integration

**Goal:** Expose PatientFlow's read-side as MCP tools so AI agents (Claude Code, Claude Desktop) can query it safely.

**Key changes:**
- Add `PMS.Mcp` project using the official C# MCP SDK
- Tools: `search_patients(query)`, `get_patient(id)`, `get_billing(patient_id)`, `list_recent_events(limit)`
- Resources: read-only patient summaries
- Auth: API-key per agent, scoped to read permissions
- Full audit logging — every MCP tool call recorded
- Containerized; deployed alongside other services
- Exposed via the gateway with a separate auth path

**Done when:** Claude Code connects to your MCP server and answers "how many patients registered this week" by calling your tools.

**Pre-read:** MCP protocol specification (https://modelcontextprotocol.io), MCP C# SDK docs.

---

## Phase 12 — Compliance polish & documentation

**Goal:** Make it look like real engineering, not a hobby project.

**Key changes:**
- Rewrite the README: clear architecture diagram, runbook, contribution guide
- `docs/`: threat model, data flow diagram, **ADRs** (architecture decision records) for major choices
- `docs/runbooks/`: incident playbooks for "Kafka down", "DB primary lost", "LLM 5xx storm"
- Data retention policy + RTBE (right-to-be-erased) implementation
- **Disaster recovery**: documented RPO/RTO, backup restore drill
- **HIPAA control mapping** table (which controls map to which code/config)
- Final `v1.0` release tag

**Done when:** A senior engineer reading the README + `docs/` can understand the system in 30 minutes and trust that it's been thought through.

---

## Workflow conventions

| Convention | Detail |
|---|---|
| One branch per phase | `phase-N-short-description` (e.g., `phase-1-microservices`) |
| One PR per phase | Branch → PR → review → merge into `main` |
| Tag at completion | `v0.N-phase-N` annotated tag on the merge commit |
| Phase branches preserved | Not deleted on remote — they're historical snapshots |
| Retrospective per phase | `docs/phases/phase-N-{name}.md` with goals, changes, learnings, deferred items |
| Always push `main` first | To a brand-new empty repo, so it auto-becomes the default branch |
| Don't click the post-merge yellow banner | It creates spurious back-merge PRs |

---

## Pace

Approximately **1–2 sessions per phase**, no rush — depth of learning and credible portfolio quality are the priorities, not raw speed. Estimated total: **6 months of focused part-time work**.

The 6-month timeline assumes ~$60/month of Claude budget allocated to PatientFlow, leaving the rest of an Enterprise $500/mo cap for company work.
