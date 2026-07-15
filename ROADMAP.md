# PatientFlow Enterprise Platform — Roadmap

An 8-phase journey from a fragile learning prototype to a production-grade,
HIPAA-aware healthcare microservices platform. Every phase landed on `main`
via its own PR and is preserved as a signed tag.

---

## Scoping decisions

| Decision | Value |
|---|---|
| Deploy target | Local — Docker Compose + Minikube/kind (no cloud bill) |
| Compliance bar | HIPAA-aware simulation — audit trail, PHI redaction, per-agent auth, isolated audit channel |
| Runtime | .NET 8 LTS |
| Repo | https://github.com/Aishwarya-K-R/PatientFlow-Enterprise-Platform |

The local target means we use **operators and self-hosted equivalents** of
what cloud providers would offer: Ollama in-cluster for LLM inference,
pgvector on Postgres for embeddings, Loki + Tempo + Prometheus + Grafana
for observability. The result is a credible production simulation without
a cloud bill.

---

## Release history

Historical tag names are preserved (they point at the exact merge commits
of the branches used during development). The **Phase** column reflects
how the work reads today, as a linear 1 ? 8 story.

| Phase | Tag | Theme | Branch |
|---|---|---|---|
| **Phase 1** | `v0.1-phase-0` | Foundation cleanup | `phase-0-foundation` |
| **Phase 2** | `v0.2-phase-1` | Real microservices split | `phase-1-microservices` |
| **Phase 3** | `v0.3-phase-2` | Data ownership + Outbox | `phase-2-data-ownership` |
| **Phase 4** | `v0.4-phase-3` | Event-driven reliability | `phase-3-event-reliability` |
| **Phase 5** | `v0.4.1-cache-warming` | Cache warming | `phase-3.5-cache-warming` |
| **Phase 6** | `v0.5-phase-6` | Observability (metrics + logs + traces) | `phase-6-observability` |
| **Phase 7** | `v0.6-phase-10` | RAG with pgvector + Ollama | `phase-10-rag` |
| **Phase 8** | `v0.7-phase-11` | MCP server + audit logging | `phase-11-mcp` |

---

## Phase 1 — Foundation cleanup

**Goal:** Make the broken codebase buildable, testable, and credibly secure from a clean clone. Stop the bleeding before doing surgery.

**Delivered:**
- `.gitignore` + `.dockerignore`; untracked 1869 build/runtime artifacts.
- Secrets out of git (`.env.example`, `Kubernetes/secrets.example.yml`).
- Fixed broken `PMS.Tests.csproj` ProjectReference.
- Excluded `PMS.Tests/**` from `PMS.csproj` implicit glob.
- Replaced broken `GetPatientsSP` SQL with safe LINQ + allow-list.
- Extracted `IPatientRepository`; `PatientService` no longer touches `DbContext`.
- `[Authorize(Roles=ADMIN)]` on `/ai/ask`; pseudonymised patient names sent to LLM.
- Versioned Redis cache keys (cache-invalidation pattern).
- JSON `LoginResponse`; async `Signup`; `AskRequest` DTO; UTC for JWT expiry.

**Tag:** `v0.1-phase-0` · **Branch:** `phase-0-foundation` · **Retro:** `docs/phases/phase-0-foundation-cleanup.md`

---

## Phase 2 — Real microservices split

**Goal:** Convert the single `PMS.csproj` into a proper multi-project .NET solution. Each service becomes its own project with its own image, `Program.cs`, and `DbContext`. Remove the `if (serviceName == "X")` switching pattern.

**Delivered:**
- `PatientFlow.sln` at repo root, 5 service projects + 2 shared libraries (`PMS.Contracts`, `PMS.Common`).
- Per-service `Program.cs` — no more service-routing switch.
- Removed gRPC self-loop in Billing.
- Per-service `Dockerfile` (only builds that project).
- Namespace cleanup: `Patient_Management_System.*` ? `PatientFlow.{Service}.*`.
- Nullable warning sweep during the file moves.

**Structure:**
```
PatientFlow.sln
??? src/
?   ??? PMS.Contracts/      shared DTOs, protos, event envelopes
?   ??? PMS.Common/         logging, exception handlers, health checks
?   ??? PMS.Gateway/        YARP reverse proxy
?   ??? PMS.Auth/           own DbContext, AuthController
?   ??? PMS.Patient/        own DbContext, IPatientRepository
?   ??? PMS.Billing/        own DbContext, gRPC server, Kafka consumer
?   ??? PMS.AI/             LLM + RAG + Kafka consumer
??? tests/
    ??? PMS.Auth.Tests/
    ??? PMS.Patient.Tests/
    ??? PMS.Billing.Tests/
    ??? PMS.AI.Tests/
```

**Tag:** `v0.2-phase-1` · **Branch:** `phase-1-microservices` · **Retro:** `docs/phases/phase-1-microservices-split.md`

---

## Phase 3 — Data ownership + Outbox

**Goal:** Each service owns its data. Introduce the Outbox table pattern as the foundation for Phase 4 reliability.

**Delivered:**
- Per-service Postgres schema (`patientflow_auth`, `patientflow_patient`, `patientflow_billing`).
- FluentValidation for request DTOs.
- Unique indexes on `Patient.Email`, `User.Email`.
- **Outbox table** per service — events written transactionally with entity writes.
- File-based SQL in EF migrations (not C# string literals).
- `dotnet ef --idempotent` script generated for prod use.

**Tag:** `v0.3-phase-2` · **Branch:** `phase-2-data-ownership` · **Retro:** `docs/phases/phase-2-data-ownership.md`

---

## Phase 4 — Event-driven reliability

**Goal:** Survive broker restarts, replays, and partial failures. The outbox table from Phase 3 finally gets shipped to Kafka properly.

**Delivered:**
- **Event Envelope schema** — `EventId`, `EventType`, `Version`, `OccurredAt`, `Payload`, `Metadata`.
- **Kafka producer hardening** — `Acks=All`, `EnableIdempotence=true`, `Flush` on shutdown.
- **Manual offset commit** — `EnableAutoCommit=false`, commit only after successful processing.
- **Retry topics + DLQ** — exponential backoff (2s, 4s, 8s), poison messages sent to DLQ after 3 attempts.
- **Polly resilience policies** — timeout ? retry ? circuit breaker for gRPC and HTTP calls.
- `KafkaConsumerBase` — reusable consumer with automatic retry and DLQ handling.
- `PatientEventsConsumer` + `PatientEventsRetryConsumer` in AI service.
- `ResiliencePolicies` helper — centralised Polly configuration.

**Tag:** `v0.4-phase-3` · **Branch:** `phase-3-event-reliability` · **Retro:** `docs/phases/phase-3-event-reliability-retrospective.md`

---

## Phase 5 — Cache warming

**Goal:** Fix the cold-start problem where the AI service only had data for events consumed after deployment.

**Delivered:**
- **PatientCacheWarmer** service — runs on AI-service startup.
- Fetches all existing patients from the Patient service via HTTP.
- Loads a full snapshot into Redis (one-time warm-up).
- Marks the cache as initialised to skip on subsequent restarts.
- Kafka consumers provide incremental updates to the pre-warmed cache.
- `GET /api/patients/all` endpoint in the Patient service (SYSTEM role only).
- `PatientSnapshotDto` — lightweight DTO for cache warming.

**Architecture:** hybrid cache-warm + event-driven updates.
- **Startup:** full snapshot from DB ? Redis (one-time).
- **Runtime:** incremental updates via Kafka events (real-time).
- **Pattern:** cache-aside with event invalidation (Netflix / Facebook / Amazon).

**Tag:** `v0.4.1-cache-warming` · **Branch:** `phase-3.5-cache-warming` · **Retro:** `docs/phases/phase-3.5-cache-warming.md`

---

## Phase 6 — Observability

**Goal:** Know what's happening in production. Traces, logs, metrics, alerts.

**Delivered:**
- **OpenTelemetry SDK** across all services with W3C TraceContext propagation.
- OTLP exporter to **Tempo**; Grafana wired to it.
- **Serilog** enriched with `TraceId`/`SpanId`; logs shipped to **Loki**.
- Grafana dashboards-as-code in `observability/grafana/dashboards/*.json`.
- Prometheus alert rules (`alerts.yml`) + Alertmanager.
- Business metrics: `patients_created_total`, `billing_failures_total`, `llm_request_latency_seconds`, cache hit ratio.

**Result:** click a slow gateway request in Grafana and follow the trace all the way through Patient ? Kafka ? Billing.

**Tag:** `v0.5-phase-6` · **Branch:** `phase-6-observability`

---

## Phase 7 — RAG with pgvector + Ollama

**Goal:** Replace "stuff every patient into the prompt" with a real retrieval pipeline.

**Delivered:**
- **pgvector** extension on Postgres for patient embeddings.
- Embedding pipeline on `PatientCreated`/`Updated`/`Deleted` events via Ollama (nomic-embed-text).
- Startup **embedding backfill** for pre-existing patients.
- `AIController` does **top-K vector search** instead of dumping all patients.
- **HNSW cosine index** for fast semantic retrieval.
- **Ollama in-cluster** as a Deployment (proper service URL, no `host.docker.internal`).
- **PromptSanitizer** defence against LLM prompt injection.
- Dedicated consumers for `patient-updated`, `patient-deleted`, `billing-created` so scaling and ordering can be tuned per topic.

**Result:** AI answers stay accurate as patient count grows; no full PHI leaves the trust boundary.

**Tag:** `v0.6-phase-10` · **Branch:** `phase-10-rag`

---

## Phase 8 — MCP server + audit logging

**Goal:** Expose PatientFlow's read side as MCP tools so AI agents (Claude Desktop, Claude Code, Copilot, custom bots) can query it safely — with a full HIPAA-style audit trail.

**Delivered:**
- New `PMS.Mcp` project using the official C# MCP SDK (`ModelContextProtocol` 1.4.1).
- Read-only `McpPatient` / `McpBilling` DbContexts and `McpReadRepository` facade.
- **Per-agent API-key auth** with `mcp:read` policy — every call attributable and independently revocable.
- **Tools:** `search_patients`, `get_patient`, `get_billing`, `list_recent_events`.
- **Resources:** `patients/summary`, `patients/{id}`, `billing/summary`, `events/recent`.
- **Audit channel:** dedicated Serilog `SourceContext=MCP.Audit` sink; `AuditEntry`, `AuditLogger`, and `McpAuditMiddleware` capture every tool call and every HTTP request (including auth failures and tool crashes).
- **Gateway integration:** `/mcp/{**catch-all}` YARP route with `PathRemovePrefix` so external clients hit one hostname while the MCP container keeps its natural `/` and `/sse` endpoints.
- **Containerisation:** docker-compose service + Kubernetes Deployment/Service with dual-DB conn strings, per-agent API keys via Secret, `/health` readiness/liveness probes; CI builds and pushes `aishwaryakr/mcp-service:latest`.
- **Bonus write-side fix:** `PMS.Billing/PatientDeletedConsumer` closes the gap where deleted patients left orphaned BillingAccount rows — the drift was originally spotted through the new MCP `billing/summary` resource.

**Result:** Claude Desktop connects via `http://localhost:5000/mcp`, lists 4 tools + 3 resources + 1 template, and every call produces an audit line queryable in Grafana with `{service="mcp"} | json | SourceContext="MCP.Audit"`.

**Tag:** `v0.7-phase-11` · **Branch:** `phase-11-mcp`

---

## Workflow conventions

| Convention | Detail |
|---|---|
| One branch per phase | `phase-N-short-description` |
| One PR per phase | Branch ? PR ? review ? merge into `main` |
| Tag at completion | Annotated tag on the merge commit |
| Phase branches preserved | Never deleted on remote — they're historical snapshots |
| Retrospective per phase | `docs/phases/phase-N-{name}.md` where applicable |

The historical tag names (`v0.1-phase-0` … `v0.7-phase-11`) reflect the
original working numbering during development. The **release history** table
above maps them to the linear 1 ? 8 phase names used throughout this document.

---

## What's next (not yet started)

These were candidates that were deprioritised to keep scope tight; they'd
be natural follow-ups if the project continues:

- **Security hardening** — refresh tokens with revocation, account lockout, partitioned rate limiter, non-root containers, OWASP baseline.
- **TLS + column-level PHI encryption** — cert-manager, Kafka SASL_SSL, Postgres SSL, encrypted PHI columns.
- **Real tests with Testcontainers** — per-test-class isolated Postgres/Redis/Kafka, coverage threshold enforced in CI, k6 smoke load test.
- **Kubernetes hardening** — Helm/Kustomize, HPA + PDB, NetworkPolicies, CloudNativePG operator, Strimzi Kafka, EF migrations as pre-deploy Job.
- **CI/CD productionisation** — Trivy scans, SBOM, Cosign signing, semver tags, GitOps via ArgoCD, Sealed Secrets / SOPS.
- **Compliance polish** — threat model, ADRs, runbooks, HIPAA control mapping, DR drill with documented RPO/RTO.
