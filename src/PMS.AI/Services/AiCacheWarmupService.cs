using PatientFlow.Contracts.Dtos;

namespace PatientFlow.AI.Services;

/// <summary>
/// Background service that warms Redis cache with patient data on AI service startup.
/// Runs once when the service starts to ensure historical patient context is available
/// for LLM queries. After warmup, Kafka consumers handle incremental updates in real time.
///
/// Cache contents are PHI-minimised (pseudonymous IDs + age only) — see BuildPatientContext.
///
/// Pattern: Hybrid Cache Warming (Startup snapshot + Event stream)
/// - Startup    : Full snapshot via HTTP GET /api/patients/all
/// - Runtime    : Incremental updates via Kafka events (PatientEventsConsumer)
/// - Admin      : Manual re-warm via POST /ai/admin/warmup
/// </summary>
public class AiCacheWarmupService : BackgroundService
{
    // Marker key stored in Redis to indicate warmup has already run.
    // Uses RedisService's "raw" string-key overload so it lives at this exact key
    // (no patient-context-v2 prefix), making it easy to find and clear during ops.
    private const string InitializedFlagKey = "ai:cache_initialized";
    private const string InitializedFlagValue = "true";

    // Long TTL on the init marker — effectively permanent, but lets abandoned dev
    // environments eventually drop it.
    private static readonly TimeSpan InitializedFlagTtl = TimeSpan.FromDays(365);

    // Brief startup pause so downstream dependencies (Patient service, Redis) finish
    // their own startup before we start hammering them.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);

    // Progress logging cadence during bulk warmup.
    private const int ProgressLogEvery = 100;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AiCacheWarmupService> _logger;

    public AiCacheWarmupService(
        IServiceProvider serviceProvider,
        ILogger<AiCacheWarmupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AI Cache Warmup Service starting...");

        await Task.Delay(StartupDelay, stoppingToken);
        await WarmupCacheAsync(stoppingToken);

        _logger.LogInformation("AI Cache Warmup Service completed");
    }

    /// <summary>
    /// Performs cache warmup. Idempotent — checks the InitializedFlag before
    /// pulling a full snapshot from Patient service. Callable from startup
    /// (this BackgroundService) or from the admin endpoint.
    /// </summary>
    public async Task WarmupCacheAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<RedisService>();
        var patientClient = scope.ServiceProvider.GetRequiredService<PatientServiceClient>();

        try
        {
            if (await IsAlreadyInitialisedAsync(redis))
            {
                _logger.LogInformation("Cache already initialised, skipping warmup");
                return;
            }

            _logger.LogInformation("Starting cache warmup...");

            var patients = await patientClient.GetAllPatientsAsync(cancellationToken);

            if (patients.Count == 0)
            {
                _logger.LogWarning("No patients found; marking cache as initialised anyway");
                await MarkAsInitialisedAsync(redis);
                return;
            }

            _logger.LogInformation("Loading {Count} patients into Redis...", patients.Count);

            var loaded = 0;
            foreach (var patient in patients)
            {
                try
                {
                    // Use the SAME key scheme + prefix the consumers + AIController use.
                    // RedisService.SetPatientContextAsync(int, string) writes under
                    // "patient-context-v2:{id}". If we used a different key here, the
                    // warmed entries would be invisible to AIController.GetAllPatientContextsAsync.
                    // No TTL on individual entries — Kafka events drive invalidation
                    // (PatientUpdated overwrites, PatientDeleted clears).
                    await redis.SetPatientContextAsync(patient.Id, BuildPatientContext(patient));

                    loaded++;

                    if (loaded % ProgressLogEvery == 0)
                    {
                        _logger.LogInformation(
                            "Progress: {Loaded}/{Total} patients", loaded, patients.Count);
                    }
                }
                catch (Exception ex)
                {
                    // One bad row shouldn't tank the whole warmup.
                    _logger.LogError(ex, "Failed to cache patient {Id}", patient.Id);
                }
            }

            await MarkAsInitialisedAsync(redis);

            _logger.LogInformation(
                "Cache warmup completed. Loaded {Loaded}/{Total} patients.",
                loaded, patients.Count);
        }
        catch (HttpRequestException ex)
        {
            // Patient service unreachable — log and continue. Kafka events
            // (PatientCreated/Updated/Deleted) will incrementally hydrate the
            // cache once the service comes online. Crashing here would put us
            // in a restart loop and starve the rest of the AI service.
            _logger.LogError(ex,
                "Failed to reach Patient Service during warmup: {Message}. " +
                "Cache will hydrate incrementally via Kafka events.",
                ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during warmup: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Build the pseudonymised context string an LLM will see for a single patient.
    /// PHI minimisation: never include real name, email, or address in the LLM prompt.
    /// This restores the rule established in Phase 0's ContextService.
    /// </summary>
    private static string BuildPatientContext(PatientDto patient)
    {
        var age = CalculateAge(patient.DateOfBirth);
        var pseudonym = $"P-{patient.Id:D5}";
        return $"Patient: {pseudonym}, Age: {age}";
    }

    private static int CalculateAge(DateOnly dateOfBirth)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age))
        {
            age--;
        }
        return age;
    }

    private static async Task<bool> IsAlreadyInitialisedAsync(RedisService redis)
    {
        var value = await redis.GetPatientContextAsync(InitializedFlagKey);
        return value == InitializedFlagValue;
    }

    private static Task MarkAsInitialisedAsync(RedisService redis) =>
        redis.SetPatientContextAsync(InitializedFlagKey, InitializedFlagValue, InitializedFlagTtl);
}
