# Phase 0 — Foundation Cleanup

**Goal:** Make the existing code base buildable, testable, and credibly secure from a clean clone — without restructuring architecture. Stop the bleeding before doing surgery.

**Status:** ✅ Complete
**Duration:** 1 working session
**Branch / tag:** `phase-0-foundation` → tag `v0.1-phase-0` at completion

---

## 1. Starting state (the audit)

A full code review identified the following as **blocking issues** that would either make the project not build, leak secrets, or fail at runtime under any real user load.

### 🔴 Critical defects

| # | Defect | Where | Severity |
|---|---|---|---|
| 1 | Secrets (JWT key, DB credentials) committed to git in `Kubernetes/secrets.yml` | Repo root | Critical |
| 2 | No `.gitignore` — `bin/`, `obj/`, `db-data/`, `.vs/`, `logs/` all tracked | Repo root | High |
| 3 | Test project reference points to non-existent file (`..\Patient Management System.csproj`) — build fails from a clean clone | `PMS.Tests/PMS.Tests.csproj` | High |
| 4 | `PatientService.GetPatientsAsync` calls a Postgres function `GetPatientsSP` that no migration creates — 500 at runtime | `Services/PatientService.cs` | High |
| 5 | `POST /ai/ask` has no `[Authorize]` — anyone can query patient data via the LLM | `Controllers/AIController.cs` | Critical |

### 🟡 Other code smells observed (deferred to later phases)

- Logging templates use string interpolation (`$"...{x}..."`) — defeats Serilog structured logging. → Phase 6
- Nullable warnings throughout (15 across the codebase). → Phase 1 sweep during restructure
- Namespace `Patient_Management_System.*` vs `<RootNamespace>PMS</RootNamespace>` — mismatch. → Phase 1
- DBContext directly injected into services — coupling that limits testability. → Partially addressed in Phase 0 for `PatientService`; rest in Phase 1
- `db.Database.Migrate()` runs on every app startup — unsafe with multiple replicas in prod. → Phase 8
- `Patient.Email` has no unique DB index — race condition between concurrent inserts. → Phase 2
- `KafkaProducer` doesn't dispose or flush — messages can be lost on shutdown. → Phase 3
- gRPC `BillingGrpcClient` calls billing-service from inside billing-service itself (self-loop). → Phase 1 (microservices split eliminates)
- ~1948 tracked files of which ~1873 were build artifacts / DB data → cleaned up

---

## 2. What we changed (step by step)

### Step 1 — `.gitignore` + `.dockerignore` + untrack 1869 files

**Added:**
- `.gitignore` — commented per category (build outputs, IDE, runtime data, secrets, OS, Docker)
- `.dockerignore` — keeps Docker build context lean and prevents accidentally baking secrets into image layers

**Untracked from git (but kept on disk):**
- `bin/`, `obj/`, `PMS.Tests/bin/`, `PMS.Tests/obj/` — build outputs
- `db-data/` — Postgres binary data files (1288 files!)
- `logs/` — Serilog runtime output
- `PMS.Tests/.DS_Store` — macOS metadata stray

**Key concept learned:** `.gitignore` only blocks *new* files from being tracked; already-tracked files need `git rm --cached` to be removed from the index. The `--cached` flag removes from git only — file stays on disk.

**Before:** 1948 tracked files. **After:** 79.

---

### Step 2 — Sanitize secrets

**Created (tracked placeholder templates):**
- `.env.example` — every variable referenced in `docker-compose.yml`, with placeholders
- `Kubernetes/secrets.example.yml` — placeholder K8s Secret manifest

**Created locally (gitignored):**
- `.env` — real local values, JWT secret generated via PowerShell's `RandomNumberGenerator.Create().GetBytes(48)` → base64 (384 bits of entropy)

**Untracked (now in `.gitignore`):**
- `Kubernetes/secrets.yml` — previously contained a weak human-readable placeholder JWT key (now untracked + replaced with a CSPRNG-generated key in the local-only `.env`)

**Pattern established:** the `.example` convention for **every** sensitive config file. Local file = real values, gitignored. Tracked file = placeholders, named `*.example.*`.

**Key concept learned:** Once a secret is in git history, rotation is the priority, not history rewriting. For this project (only placeholder secrets), history can stay; for any real leak, rotate first then optionally scrub with `git filter-repo`.

---

### Step 3 — Fix broken test project reference

**Changed:** `PMS.Tests/PMS.Tests.csproj` ProjectReference from `..\Patient Management System.csproj` → `..\PMS.csproj`.

**Discovered a second bug while fixing the first:** the .NET SDK's implicit compile glob (`**/*.cs`) was sweeping `PMS.Tests/**/*.cs` into the main `PMS.csproj` build (because `PMS.Tests/` is a subdirectory). This caused duplicate-AssemblyInfo errors once both projects had `obj/` outputs.

**Fix:** Added an `<ItemGroup>` to `PMS.csproj` excluding `PMS.Tests/**` from `Compile`, `Content`, `EmbeddedResource`, and `None`. Comment notes this is a workaround until Phase 1 moves the test project to a sibling directory.

**Why the bug was previously hidden:**
- `PMS.csproj` alone (without `PMS.Tests/obj/Release/`) builds fine — only one AssemblyInfo gets swept in, no duplication.
- Docker builds only `PMS.csproj`, so production images built successfully despite the broken test project.
- Tests likely "worked" in Visual Studio because the committed `PMS.Tests/bin/` had pre-built DLLs that the IDE used as fallback.

**Key concept learned:** CI is the only honest broker. If your CI doesn't run `dotnet test` on a fresh clone, you don't actually know your tests work. Phase 9 will add this gate.

---

### Step 4 — Replace `GetPatientsSP` call with safe LINQ + allow-list

**Replaced** the raw-SQL call `_context.Patients.FromSqlInterpolated($"SELECT * FROM GetPatientsSP({search}, {sortCol}, {sortDir}, {pageNo}, {pageSize})")` with EF Core LINQ.

**New implementation includes:**
- `Math.Max(1, pageNo)` and `Math.Clamp(pageSize, 1, 100)` to prevent DoS via huge page sizes or negative offsets.
- Case-insensitive search using Postgres-native `EF.Functions.ILike` on Name OR Email.
- A `switch` allow-list of sortable columns (`Name`, `Email`, `RegisteredDate`, `DateOfBirth`); anything else falls through to `Id`. **Unknown user input is discarded, not sanitized** — no path to SQL injection.
- Direction (`asc`/`desc`) handled via an `isDesc` flag separate from the column to avoid a cartesian-product switch.

**Why LINQ over recreating the SP via migration:**
- LINQ is type-safe at compile time — column renames break compile, not runtime.
- No additional migration to maintain.
- EF Core's translation is equivalent in performance for this query.
- SPs make sense when query is genuinely complex, when multiple non-.NET clients consume it, or in regulated environments where DBAs own SQL — none of those apply here.

**Key concepts learned:**
- **Make invalid states unrepresentable.** Allow-lists *discard* unknown values before they touch SQL; sanitization is a weaker pattern.
- **Defer execution in LINQ.** `AsQueryable()` builds an expression tree; SQL only generates and runs on `.ToListAsync()`. This is what makes conditional `Where`/`OrderBy` chaining safe.
- **DB schema changes (including SPs) in prod** don't live inside app images — they're applied at runtime via a controlled pre-deploy step (K8s Job / migration tool), once per release, before app pods start. Phase 8 will wire this up.

---

### Step 4.5 (added on user request) — Extract `IPatientRepository`

**Created:**
- `Data/IPatientRepository.cs` — interface with 6 methods (`SearchAsync`, `GetByIdAsync`, `GetByEmailAsync`, `AddAsync`, `UpdateAsync`, `DeleteAsync`)
- `Data/PatientRepository.cs` — EF Core implementation

**Refactored:** `Services/PatientService.cs` to depend on `IPatientRepository` instead of `AppDbContext`. Service now contains only business logic (validation, dup-email rule, caching, Kafka publishing, cache invalidation). Repository owns all EF Core interaction.

**DI registered** in `Program.cs`: `AddScoped<IPatientRepository, PatientRepository>()`.

**Why we did this in Phase 0 rather than deferring to Phase 1:**
- User explicitly requested it as a separation-of-concerns improvement.
- The interface becomes the natural mock seam for Phase 7 unit tests.
- It pre-establishes the boundary that Phase 1's microservices split will preserve when `PMS.Patient` becomes its own project.

**Why we *didn't* extract `IUserRepository`, `IBillingRepository`, etc. too:**
- Phase 1 will split into per-service projects anyway — extracting now means doing the work twice.
- Phase 0 scope was deliberately tight; one repo extraction was already a stretch.

**Honest scorecard on whether the Repository was worth it:**
- ✅ Easier unit-test mocking (real benefit when Phase 7 lands)
- ✅ Centralized defense at the DB boundary (page-size clamping)
- ✅ Stable seam for microservice extraction (Phase 1)
- ⚠️ Doesn't fully hide EF specifics — `EF.Functions.ILike` still leaks the provider, which we accept honestly
- ⚠️ ~95 lines of new code for a still-simple aggregate

**Key concept learned:** Generic `IRepository<T>` over EF Core is an anti-pattern; specific repos with intent-revealing methods are the modern .NET approach. Modern Microsoft guidance is "DbContext IS your repository" — adding another layer is optional and contextual.

---

### Step 5 — Lock down `/ai/ask` + PHI masking

**Three independent changes:**

1. **Authorization:** Added `[Authorize(Roles = "ADMIN")]` to `AIController.Ask`. Anonymous → 401, non-admin authenticated → 403.

2. **PHI minimization in `ContextService.GetPatientContextDictAsync`:**
   - Removed `p.Name` from the select projection — names never leave the application boundary into the LLM.
   - Replaced with a pseudonym format `P-{Id:D5}` (`P-00042`, `P-00001`, etc.) so the LLM can still reference patients consistently.

3. **Audit logging:**
   - Added the calling user's ID (`User.FindFirst(ClaimTypes.NameIdentifier)?.Value`) to the log line.
   - Logged `answerChars: readableAnswer.Length` instead of the full answer body, since the answer could contain PHI in error.
   - Prefixed log line with `AUDIT ai-query` for future filterability.

4. **Cache invalidation via key versioning:** Bumped Redis key prefix from `patient-context:` to `patient-context-v2:` in `RedisService` (encapsulated as a `private const string ContextKeyPrefix`). Old cached entries (which still contain real names) are now orphaned — no code reads them. New writes use the new format.

**Key concepts learned:**
- **Defense in depth** — auth at the controller boundary, pseudonymization at the data layer, cache invalidation by versioned key. Three independent layers.
- **PHI minimization > sanitization.** Don't remove names mid-string; never select them at the data layer in the first place. The cleanest place to enforce a constraint is where the data is *fetched*, not where it's *used*.
- **User identifiers in audit logs are required, not a leak.** `userId` is an authentication identifier, not PHI. The whole point of audit is to answer "who did what when" — without a user identifier, the log is useless.
- **Cache key versioning** is the standard pattern for invalidating cached content when the content's *shape or policy* changes. Bump the prefix; let old entries rot harmlessly.

---

### Step 6 — Fix API contracts

| Endpoint | Before | After |
|---|---|---|
| `POST /auth/login` response | `"Login successful !!! Token: " + token` (concatenated string) | JSON: `{ "accessToken": "...", "expiresAt": "..." }` |
| `POST /auth/signup` save | `_context.SaveChanges()` synchronous | `await _context.SaveChangesAsync()` |
| `POST /ai/ask` request body | `[FromBody] string request` (client posts `"my question"` with quotes) | `[FromBody] AskRequest { Question }` — natural JSON |
| `AuthService.Login` return type | `Task<string>` | `Task<LoginResponse?>` |
| JWT generation time basis | `DateTime.Now` (local) | `DateTime.UtcNow` (correct — JWT `exp` is always UTC) |
| Token lifetime | Hardcoded `1` in `AddHours(1)` | `private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1)` |

**Test updated:** `PatientTests.GetPatients_Should_Return_200_With_Valid_Token` now uses `ReadFromJsonAsync<LoginResponse>()` instead of `responseString.Split("Token: ")[1]`.

**Key concepts learned:**
- **Records for response DTOs** (immutable, value-equality). **Classes for request DTOs** (data annotations, model binder ergonomics).
- **`ActionResult<T>` over `ActionResult`** — generic version lets Swagger document the response shape.
- **UTC for everything time-related.** Mixing local and UTC times is one of the most common production bugs.
- **API contracts are public.** When you change one, every consumer (including tests) must update.

---

### Step 7 — Clean-clone verification

Simulated a fresh `git clone` experience: wiped all `bin/obj`, ran `dotnet restore`, `dotnet build`, `dotnet test --list-tests`.

**Results:** ✅ 0 build errors. ✅ 6 tests discovered. ✅ No JWT secret in any tracked file. ✅ No real credentials in any tracked file. ✅ Tracked file count 1948 → 79.

---

## 3. Architectural decisions made in Phase 0

1. **Repository pattern** introduced for the Patient aggregate. Going forward, all new aggregate work uses concrete `I{X}Repository` interfaces in `Data/`. Do **not** introduce a generic `IRepository<T>`.

2. **Per-method `SaveChangesAsync()` in the repo** is acceptable for single-entity operations. When Phase 2 adds the Outbox pattern, an `IUnitOfWork` will be introduced so an outbox row and an entity write can share one transaction.

3. **DTOs live in `Models/Dtos.cs`** for now. Phase 1 may move them to a `PMS.Contracts` shared project when services are split.

4. **No interfaces over services** (`PatientService`, `AuthService`, etc.) — kept concrete. The Repository is the seam for testing; the service is the seam for orchestration. Adding an `IPatientService` interface would be ceremonial.

5. **The pre-deploy migration pattern** is documented as the production path even though `Program.cs` still runs `db.Database.Migrate()` on startup. Phase 8 will replace this with a K8s Job.

---

## 4. Top 10 takeaways for portfolio interviews

1. **Phase 0 is hygiene; Phase 1+ is architecture.** A clean foundation is non-negotiable before any restructure.
2. **`.gitignore` and `git rm --cached` are different operations.** The first blocks future tracking; the second removes already-tracked files from the index.
3. **`.dockerignore` exists for security as much as for speed.** Without it, `COPY . .` can bake `.env` into image layers permanently.
4. **The `.example` convention** is the universal pattern for sensitive config files. One file for real values (gitignored), one for placeholders (tracked).
5. **A broken `<ProjectReference>` is a silent failure at restore-time but loud at build-time.** Always confirm with `dotnet build`, not `dotnet restore`.
6. **LINQ allow-lists are stronger than SQL parameterization.** Bad input is discarded, not sanitized.
7. **Schema changes in prod don't live in images.** They're applied at runtime via a controlled pre-deploy Job, exactly once per release.
8. **PHI minimization at the data layer beats masking at the boundary.** Never select what you don't need.
9. **User identifiers in audit logs are required by HIPAA, not a leak.** PHI ≠ identity infrastructure.
10. **Cache key versioning** is the production pattern for invalidating cached PII after a content policy change.

---

## 5. Verification at end of Phase 0

| Check | Result |
|---|---|
| `dotnet restore` from clean clone | ✅ |
| `dotnet build` (both projects) | ✅ 0 errors, 15 pre-existing nullable warnings |
| `dotnet test --list-tests` | ✅ 6 tests discovered |
| JWT secret in any tracked file | ✅ Not present |
| Tracked file count | ✅ 1948 → 79 |
| All Phase 0 critical defects resolved | ✅ |
