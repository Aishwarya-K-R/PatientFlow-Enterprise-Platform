using System.Text.Json;

namespace PatientFlow.AI.Services;

/// <summary>
/// Warms the Redis cache with all existing patients on AI service startup.
/// This ensures the AI service has complete historical context, not just events
/// consumed after startup. After initial warm-up, Kafka events keep cache updated.
/// 
/// Pattern: Cache Warming + Event-Driven Updates (Hybrid Approach)
/// - Startup: Fetch full snapshot from Patient Service (one-time)
/// - Runtime: Incremental updates via Kafka events (real-time)
/// </summary>
public class PatientCacheWarmer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PatientCacheWarmer> _logger;
    private readonly IConfiguration _config;

    public PatientCacheWarmer(
        IServiceProvider serviceProvider,
        ILogger<PatientCacheWarmer> logger,
        IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting patient cache warm-up...");

        using var scope = _serviceProvider.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<RedisService>();

        try
        {
            // Check if cache is already initialized
            var isInitialized = await redis.GetPatientContextAsync("_cache_initialized");
            if (isInitialized == "true")
            {
                _logger.LogInformation("Cache already initialized, skipping warm-up");
                return;
            }

            // Fetch all patients from Patient Service
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();

            var patientServiceUrl = _config["PatientService:Url"] ?? "http://patient-service:5001";
            var endpoint = $"{patientServiceUrl}/api/patients/all";

            _logger.LogInformation("Fetching patient snapshot from {Endpoint}", endpoint);

            var response = await httpClient.GetAsync(endpoint, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Failed to fetch patients: {response.StatusCode} - {errorContent}");
            }

            var patients = await response.Content.ReadFromJsonAsync<List<PatientSnapshotDto>>(
                cancellationToken: cancellationToken);

            if (patients == null || patients.Count == 0)
            {
                _logger.LogWarning("No patients found in database, skipping cache warm-up");
                await MarkAsInitialized(redis);
                return;
            }

            _logger.LogInformation("Loading {Count} patients into Redis cache...", patients.Count);

            // Load patients into Redis
            var loadedCount = 0;
            foreach (var patient in patients)
            {
                try
                {
                    var context = BuildPatientContext(patient);
                    await redis.SetPatientContextAsync(
                        $"patient:{patient.PatientId}",
                        context,
                        TimeSpan.FromDays(1));  // 1-day expiry, refreshed by events

                    loadedCount++;

                    // Log progress every 100 patients
                    if (loadedCount % 100 == 0)
                    {
                        _logger.LogInformation("Loaded {Count}/{Total} patients...",
                            loadedCount, patients.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load patient {PatientId} into cache",
                        patient.PatientId);
                    // Continue with other patients
                }
            }

            // Mark cache as initialized
            await MarkAsInitialized(redis);

            _logger.LogInformation(
                "Cache warm-up completed successfully! Loaded {Count} patients in Redis",
                loadedCount);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "Failed to fetch patients from Patient Service: {Message}", ex.Message);
            throw;  // Fail startup - AI service needs patient data to function
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during cache warm-up: {Message}", ex.Message);
            throw;  // Fail startup
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Patient cache warmer stopping");
        return Task.CompletedTask;
    }

    private static string BuildPatientContext(PatientSnapshotDto patient)
    {
        return $"Patient {patient.PatientId} named {patient.Name}, " +
               $"born on {patient.DateOfBirth:yyyy-MM-dd}, " +
               $"email {patient.Email}, " +
               $"address {patient.Address}";
    }

    private static async Task MarkAsInitialized(RedisService redis)
    {
        await redis.SetPatientContextAsync(
            "_cache_initialized",
            "true",
            TimeSpan.FromDays(365));  // Practically never expires
    }
}

/// <summary>
/// DTO for patient snapshot from Patient Service.
/// Matches the PatientSnapshotDto from Patient Service.
/// </summary>
public record PatientSnapshotDto
{
    public int PatientId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateOnly DateOfBirth { get; init; }
    public string Address { get; init; } = string.Empty;
}
